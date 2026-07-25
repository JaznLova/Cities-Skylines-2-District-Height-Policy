using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Enforces per-district height policies by marking non-conforming residential buildings
    // Deleted, which makes the zone spawner reroll the lot.
    //
    // This deliberately runs as a real GameSystemBase rather than as a Harmony postfix on
    // ZoneSpawnSystem.OnUpdate: doing the entity work inside another system's update meant
    // creating queries mid-group, force-completing that system's freshly scheduled jobs, and
    // playing an EntityCommandBuffer back immediately — a main-thread structural change in the
    // middle of GameSimulation. That left Game.SafeCommandBufferSystem unable to hand out a
    // buffer to the next system, surfacing as
    // "Trying to create EntityCommandBuffer when it's not allowed!" from UpdateGroupSystem.
    // Here the query is cached in OnCreate and deletions go through EndFrameBarrier — the same
    // barrier ZoneSpawnSystem itself uses — which plays them back at a point the game considers
    // safe.
    public partial class DistrictHeightPolicySystem : GameSystemBase
    {
        private EndFrameBarrier m_Barrier;
        private EntityQuery m_BuildingQuery;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<ResidentialProperty>(),
                    ComponentType.ReadOnly<CurrentDistrict>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                // Buildings already queued for deletion must not be re-judged — they would
                // burn a second reroll against their lot before the barrier plays back.
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            RequireForUpdate(m_BuildingQuery);
        }

        // The policy only has to catch buildings shortly after they spawn; running every
        // simulation tick is wasted work on a large city.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        private long PositionKey(Entity entity)
        {
            var pos = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            long x = (long)System.Math.Round((double)pos.x);
            long z = (long)System.Math.Round((double)pos.z);
            return (x << 20) ^ (z & 0xFFFFF);
        }

        protected override void OnUpdate()
        {
            if (DistrictPolicyStore.DistrictTiers.Count == 0) return;

            var em = EntityManager;

            var entities  = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            var districts = m_BuildingQuery.ToComponentDataArray<CurrentDistrict>(Allocator.Temp);
            var prefabs   = m_BuildingQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            // Grandfather all pre-existing buildings when a district first gets a policy.
            foreach (var kvp in DistrictPolicyStore.DistrictTiers)
            {
                if (kvp.Value.Count == 0) continue;
                if (LotPolicyState.ActivatedDistricts.Contains(kvp.Key)) continue;

                LotPolicyState.ActivatedDistricts.Add(kvp.Key);
                for (int i = 0; i < entities.Length; i++)
                {
                    if (districts[i].m_District == kvp.Key)
                        LotPolicyState.ApprovedEntities.Add(entities[i]);
                }
                Mod.log.Info($"[DistrictHeightPolicySystem] Grandfathered existing buildings in district {kvp.Key.Index}");
            }

            var ecb = m_Barrier.CreateCommandBuffer();

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (LotPolicyState.ApprovedEntities.Contains(entity)) continue;

                var districtEntity = districts[i].m_District;
                var activeTiers = DistrictPolicyStore.GetTiers(districtEntity);

                if (activeTiers == null || activeTiers.Count == 0)
                {
                    LotPolicyState.ApprovedEntities.Add(entity);
                    continue;
                }

                var prefabEntity = prefabs[i].m_Prefab;
                if (!em.HasComponent<ObjectGeometryData>(prefabEntity))
                {
                    LotPolicyState.ApprovedEntities.Add(entity);
                    continue;
                }

                var geom = em.GetComponentData<ObjectGeometryData>(prefabEntity);
                float buildingHeight = geom.m_Bounds.max.y;

                bool hasTransform = em.HasComponent<Game.Objects.Transform>(entity);
                long posKey = hasTransform ? PositionKey(entity) : 0;

                if (BuildingHeightLoader.IsBuildingAllowed(activeTiers, buildingHeight))
                {
                    // Satisfying building landed — enforce it and clear any give-up state for this lot.
                    LotPolicyState.ApprovedEntities.Add(entity);
                    if (hasTransform)
                    {
                        LotPolicyState.RerollCounts.Remove(posKey);
                        LotPolicyState.UnsatisfiableLots.Remove(posKey);
                    }
                }
                else if (hasTransform && LotPolicyState.UnsatisfiableLots.Contains(posKey))
                {
                    // This lot's density can't produce a valid asset. Under KeepBuilding that
                    // means accepting whatever spawns; under DezonePlot the lot should already
                    // be unzoned, so a building reappearing here gets the same treatment again
                    // rather than being grandfathered in.
                    ApplyFallback(entity, ecb, buildingHeight, districtEntity);
                }
                else
                {
                    int count = hasTransform
                        ? (LotPolicyState.RerollCounts.TryGetValue(posKey, out var c) ? c : 0) + 1
                        : 0;
                    if (hasTransform) LotPolicyState.RerollCounts[posKey] = count;

                    if (hasTransform && count > LotPolicyState.MaxRerolls)
                    {
                        // Given up: no valid asset exists for this lot's density. What happens
                        // next is the user's Fallback System setting.
                        LotPolicyState.UnsatisfiableLots.Add(posKey);
                        Mod.log.Debug(
                            $"[DistrictHeightPolicySystem] Lot at {posKey} unsatisfiable for policy in district {districtEntity.Index}");
                        ApplyFallback(entity, ecb, buildingHeight, districtEntity);
                    }
                    else
                    {
                        // Route the deletion through EndFrameBarrier and let the game's own
                        // culling/cleanup systems tear the entity (and its render batch) down.
                        //
                        // Do NOT also add Updated here. Updated means "re-evaluate and rebuild
                        // this entity's render batch"; on an entity that is simultaneously
                        // Deleted, the culling system re-registers a mesh instance that the
                        // cleanup system then destroys, orphaning that instance in the batch.
                        // The orphan renders as a ghost building that only clears when hover
                        // forces a batch rebuild (and fully clears on reload) — the exact
                        // "hovering shows a delete overlay but the building never goes away"
                        // symptom. The stale-reference problems Updated was meant to solve came
                        // from the old Harmony patch's immediate mid-frame playback, which the
                        // barrier already fixes.
                        ecb.AddComponent<Deleted>(entity);
                        Mod.log.Debug(
                            $"[DistrictHeightPolicySystem] Marking building {entity.Index} (height {buildingHeight:F1}m) deleted — no active tier covers it in district {districtEntity.Index}");
                    }
                }
            }

            entities.Dispose();
            districts.Dispose();
            prefabs.Dispose();
        }

        // Applied to a building sitting on a lot the policy has given up on.
        private void ApplyFallback(Entity entity, EntityCommandBuffer ecb, float buildingHeight, Entity districtEntity)
        {
            if (LotPolicyState.Fallback == FallbackMode.KeepBuilding)
            {
                LotPolicyState.ApprovedEntities.Add(entity);
                Mod.log.Debug(
                    $"[DistrictHeightPolicySystem] Keeping {buildingHeight:F1}m building {entity.Index} in district {districtEntity.Index}");
                return;
            }

            // DezonePlot: strip the zoning first, then delete the building. Without the dezone
            // the spawner would just drop another non-conforming building on the same lot.
            // Note the ghost-building rule below still applies — Updated goes on the zone block
            // entities (a different entity), never on the building being Deleted.
            if (!DezoneLot(entity, ecb))
            {
                // Couldn't find the lot's cells (no road edge, no lot data). Falling back to
                // keeping the building is better than deleting it forever in a respawn loop.
                LotPolicyState.ApprovedEntities.Add(entity);
                Mod.log.Debug(
                    $"[DistrictHeightPolicySystem] Could not dezone lot for building {entity.Index} — keeping it instead");
                return;
            }

            ecb.AddComponent<Deleted>(entity);
            Mod.log.Debug(
                $"[DistrictHeightPolicySystem] Dezoned lot and removed {buildingHeight:F1}m building {entity.Index} in district {districtEntity.Index}");
        }

        // Clears the zone type off every cell the building's lot covers, so nothing respawns
        // there. Returns false if the lot's cells couldn't be located.
        //
        // Cells live in DynamicBuffer<Game.Zones.Cell> on the zone Block entities owned by the
        // building's road edge; Block.m_Size gives the grid and ZoneUtils.GetCellPosition turns
        // a cell index into a world position. The building's footprint is the same quad the game
        // uses, BuildingUtils.CalculateCorners(transform, lotSize).
        private bool DezoneLot(Entity building, EntityCommandBuffer ecb)
        {
            var em = EntityManager;

            if (!em.HasComponent<Game.Objects.Transform>(building)) return false;
            if (!em.HasComponent<Building>(building)) return false;
            if (!em.HasComponent<PrefabRef>(building)) return false;

            var prefabEntity = em.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!em.HasComponent<BuildingData>(prefabEntity)) return false;
            var lotSize = em.GetComponentData<BuildingData>(prefabEntity).m_LotSize;

            var transform = em.GetComponentData<Game.Objects.Transform>(building);
            var lotQuad = BuildingUtils.CalculateCorners(transform, lotSize).xz;

            var roadEdge = em.GetComponentData<Building>(building).m_RoadEdge;
            if (roadEdge == Entity.Null) return false;
            if (!em.HasBuffer<Game.Zones.SubBlock>(roadEdge)) return false;

            bool changedAny = false;
            var subBlocks = em.GetBuffer<Game.Zones.SubBlock>(roadEdge);

            for (int b = 0; b < subBlocks.Length; b++)
            {
                var blockEntity = subBlocks[b].m_SubBlock;
                if (!em.HasComponent<Game.Zones.Block>(blockEntity)) continue;
                if (!em.HasBuffer<Game.Zones.Cell>(blockEntity)) continue;

                var block = em.GetComponentData<Game.Zones.Block>(blockEntity);
                var cells = em.GetBuffer<Game.Zones.Cell>(blockEntity);

                bool blockChanged = false;
                for (int z = 0; z < block.m_Size.y; z++)
                {
                    for (int x = 0; x < block.m_Size.x; x++)
                    {
                        int index = z * block.m_Size.x + x;
                        if (index >= cells.Length) continue;

                        var cell = cells[index];
                        if (cell.m_Zone.Equals(Game.Zones.ZoneType.None)) continue;

                        var cellPos = Game.Zones.ZoneUtils.GetCellPosition(block, new int2(x, z));
                        if (!Colossal.Mathematics.MathUtils.Intersect(lotQuad, cellPos.xz)) continue;

                        cell.m_Zone = Game.Zones.ZoneType.None;
                        cell.m_State &= ~(Game.Zones.CellFlags.Occupied | Game.Zones.CellFlags.Shared);
                        cells[index] = cell;
                        blockChanged = true;
                    }
                }

                if (blockChanged)
                {
                    // The block itself is safe to mark Updated — it is not the entity being
                    // Deleted, and the zone overlay/render needs the rebuild to show the change.
                    ecb.AddComponent<Updated>(blockEntity);
                    changedAny = true;
                }
            }

            return changedAny;
        }
    }
}
