using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
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
                    // This lot's density can't produce a valid asset — keep whatever spawns.
                    LotPolicyState.ApprovedEntities.Add(entity);
                }
                else
                {
                    int count = hasTransform
                        ? (LotPolicyState.RerollCounts.TryGetValue(posKey, out var c) ? c : 0) + 1
                        : 0;
                    if (hasTransform) LotPolicyState.RerollCounts[posKey] = count;

                    if (hasTransform && count > LotPolicyState.MaxRerolls)
                    {
                        // Given up: no valid asset exists for this lot's density. Keep the building.
                        LotPolicyState.UnsatisfiableLots.Add(posKey);
                        LotPolicyState.ApprovedEntities.Add(entity);
                        Mod.log.Debug(
                            $"[DistrictHeightPolicySystem] Lot at {posKey} unsatisfiable for policy — keeping {buildingHeight:F1}m building in district {districtEntity.Index}");
                    }
                    else
                    {
                        // Deleted alone tears the entity down but leaves dependent systems
                        // (render batches, zone/infoview aggregation) holding references to
                        // it — which surfaces as a building that only disappears once hover
                        // forces its batch to rebuild, and as dangling PrefabRefs in
                        // InfoviewsUISystem.BindZoneInfos. Updated is the game's signal to
                        // re-evaluate the entity, so those consumers drop it cleanly.
                        ecb.AddComponent<Deleted>(entity);
                        ecb.AddComponent<Updated>(entity);
                        Mod.log.Debug(
                            $"[DistrictHeightPolicySystem] Marking building {entity.Index} (height {buildingHeight:F1}m) deleted — no active tier covers it in district {districtEntity.Index}");
                    }
                }
            }

            entities.Dispose();
            districts.Dispose();
            prefabs.Dispose();
        }
    }
}
