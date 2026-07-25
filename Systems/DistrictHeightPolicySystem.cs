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
    // Enforces per-district height policies by marking non-conforming buildings Deleted, which
    // makes the zone spawner reroll the lot.
    //
    // Covers Residential at every density, plus Commercial and Office at HIGH density only.
    // Low density commercial/office assets top out around 10m, so any policy above the Small
    // tier would reject every candidate the spawner offers, burn through the rerolls and then
    // hand the lot to the fallback — which under the default Dezone Plot setting would strip
    // the zoning off every low density shop and office in the district. Those lots are skipped
    // outright instead. See IsHighDensityZone.
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
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_BuildingQuery;

        // Reads Platter's LinkedParcel to answer "is this building standing on a parcel". Created
        // lazily rather than in OnCreate: Platter's types are not resolvable that early, and this
        // stays null forever when Platter is not installed.
        private SingleEntityComponentReader m_LinkedParcel;
        private bool m_ParcelReadable;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                // CurrentDistrict is deliberately NOT required. A building on a Platter parcel
                // carries its own policy and must be judged wherever it stands, including outside
                // every district — and a district-policied building without the component simply
                // resolves to Entity.Null, which is the "no policy, approve" path it already took.
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                // Zoned growables only. Which of these actually get judged is decided per
                // building by IsEligible — service buildings, parks and the like carry none
                // of them and never enter the query at all.
                Any = new[]
                {
                    ComponentType.ReadOnly<ResidentialProperty>(),
                    ComponentType.ReadOnly<CommercialProperty>(),
                    ComponentType.ReadOnly<OfficeProperty>(),
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

        protected override void OnDestroy()
        {
            m_LinkedParcel?.Dispose();
            base.OnDestroy();
        }

        // Once per OnUpdate. Sets m_ParcelReadable, which is the only thing callers should test —
        // false covers "Platter absent", "not loaded yet" and "a Platter update broke the read"
        // alike, and every one of those means the same thing here: treat nothing as a parcel.
        private void RefreshParcelReader()
        {
            m_ParcelReadable = false;

            if (m_LinkedParcel == null)
            {
                var type = PlatterInterop.GetLinkedParcelComponentType(World);
                if (type == null) return;
                m_LinkedParcel = new SingleEntityComponentReader(type);
            }

            m_ParcelReadable = m_LinkedParcel.TryUpdate(this);
        }

        // The parcel this building grew on, or Entity.Null when it is on ordinary zoning.
        private Entity GetParcel(Entity building)
        {
            return m_ParcelReadable ? m_LinkedParcel.Read(EntityManager, building) : Entity.Null;
        }

        private long PositionKey(Entity entity)
        {
            var pos = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            long x = (long)System.Math.Round((double)pos.x);
            long z = (long)System.Math.Round((double)pos.z);
            return (x << 20) ^ (z & 0xFFFFF);
        }

        // Which lot a building's reroll history is filed under. A parcel override files it under
        // the parcel entity, because the position key hashes the *building's* centre and a building
        // smaller than its parcel moves between rerolls — so every attempt would get a fresh key,
        // the count would never pass 1, and the lot would never be given up on. Ordinary zoning
        // keeps the position key, which is stable there and survives the building being replaced.
        private readonly struct LotKey
        {
            public readonly Entity Parcel;      // Entity.Null for ordinary zoning
            public readonly long Position;
            public readonly bool Trackable;

            private LotKey(Entity parcel, long position, bool trackable)
            {
                Parcel = parcel;
                Position = position;
                Trackable = trackable;
            }

            public bool FromParcel => Parcel != Entity.Null;

            public static LotKey ForParcel(Entity parcel) => new LotKey(parcel, 0, true);
            public static LotKey ForPosition(long position) => new LotKey(Entity.Null, position, true);

            // No transform and no parcel: nothing stable to file history under, so the building is
            // judged each pass but never accumulates towards the fallback.
            public static LotKey None => new LotKey(Entity.Null, 0, false);
        }

        // CurrentDistrict is no longer required by the query, so it has to be read per building.
        // Absent means "in no district", which resolves to no policy — the same answer the old
        // code reached when a district had no tiers set.
        private Entity GetDistrict(Entity building)
        {
            return EntityManager.HasComponent<CurrentDistrict>(building)
                ? EntityManager.GetComponentData<CurrentDistrict>(building).m_District
                : Entity.Null;
        }

        private static string DescribeLot(LotKey key, Entity districtEntity)
        {
            if (key.FromParcel) return $"Platter parcel {key.Parcel.Index}";
            return $"lot at {key.Position} in district {districtEntity.Index}";
        }

        private static bool IsGivenUp(LotKey key)
        {
            if (!key.Trackable) return false;
            return key.FromParcel
                ? LotPolicyState.UnsatisfiableParcels.Contains(key.Parcel)
                : LotPolicyState.UnsatisfiableLots.Contains(key.Position);
        }

        private static void ClearRerollState(LotKey key)
        {
            if (!key.Trackable) return;
            if (key.FromParcel)
            {
                LotPolicyState.ParcelRerollCounts.Remove(key.Parcel);
                LotPolicyState.UnsatisfiableParcels.Remove(key.Parcel);
            }
            else
            {
                LotPolicyState.RerollCounts.Remove(key.Position);
                LotPolicyState.UnsatisfiableLots.Remove(key.Position);
            }
        }

        // Records one failed attempt and returns the running total, or 0 when there is nothing to
        // file it under.
        private static int BumpReroll(LotKey key)
        {
            if (!key.Trackable) return 0;

            if (key.FromParcel)
            {
                int n = (LotPolicyState.ParcelRerollCounts.TryGetValue(key.Parcel, out var pc) ? pc : 0) + 1;
                LotPolicyState.ParcelRerollCounts[key.Parcel] = n;
                return n;
            }

            int count = (LotPolicyState.RerollCounts.TryGetValue(key.Position, out var c) ? c : 0) + 1;
            LotPolicyState.RerollCounts[key.Position] = count;
            return count;
        }

        private static void MarkGivenUp(LotKey key)
        {
            if (!key.Trackable) return;
            if (key.FromParcel) LotPolicyState.UnsatisfiableParcels.Add(key.Parcel);
            else LotPolicyState.UnsatisfiableLots.Add(key.Position);
        }

        protected override void OnUpdate()
        {
            // Parcel overrides apply outside districts too, so a city with no district policy at
            // all still has work to do here.
            if (DistrictPolicyStore.DistrictTiers.Count == 0 && ParcelOverrideStore.Count == 0) return;

            RefreshParcelReader();

            var em = EntityManager;

            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            var prefabs  = m_BuildingQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            bool useOverrides = LotPolicyState.PlatterIntegration && ParcelOverrideStore.Count > 0;

            // Grandfather all pre-existing buildings when a district first gets a policy.
            foreach (var kvp in DistrictPolicyStore.DistrictTiers)
            {
                if (kvp.Value.Count == 0) continue;
                if (LotPolicyState.ActivatedDistricts.Contains(kvp.Key)) continue;

                LotPolicyState.ActivatedDistricts.Add(kvp.Key);
                for (int i = 0; i < entities.Length; i++)
                {
                    if (GetDistrict(entities[i]) != kvp.Key) continue;

                    // A building on an overridden parcel is exempt from grandfathering: the parcel's
                    // restriction is a deliberate per-parcel choice, and approving it here would
                    // make that choice unenforceable for good the moment the district gained a
                    // policy.
                    if (useOverrides && ParcelOverrideStore.Has(GetParcel(entities[i]))) continue;

                    LotPolicyState.ApprovedEntities.Add(entities[i]);
                }
                Mod.log.Info($"[DistrictHeightPolicySystem] Grandfathered existing buildings in district {kvp.Key.Index}");
            }

            var ecb = m_Barrier.CreateCommandBuffer();

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (LotPolicyState.ApprovedEntities.Contains(entity)) continue;

                // The parcel's own restriction is resolved first and wins outright. It has to come
                // before the district lookup, because a parcel outside every district — or in an
                // unpolicied one — would otherwise hit the "no tiers, approve" path below and be
                // approved permanently before its own policy was ever consulted.
                var parcel = useOverrides ? GetParcel(entity) : Entity.Null;
                var activeTiers = parcel != Entity.Null ? ParcelOverrideStore.GetTiers(parcel) : null;
                bool fromParcel = activeTiers != null && activeTiers.Count > 0;

                var districtEntity = Entity.Null;
                if (!fromParcel)
                {
                    districtEntity = GetDistrict(entity);
                    activeTiers = DistrictPolicyStore.GetTiers(districtEntity);

                    if (activeTiers == null || activeTiers.Count == 0)
                    {
                        LotPolicyState.ApprovedEntities.Add(entity);
                        continue;
                    }
                }

                var prefabEntity = prefabs[i].m_Prefab;
                if (!em.HasComponent<ObjectGeometryData>(prefabEntity))
                {
                    LotPolicyState.ApprovedEntities.Add(entity);
                    continue;
                }

                // The low-density gate exists to stop a district-wide policy dezoning every small
                // shop and office, whose assets top out around 10m. A parcel override is the
                // opposite situation — one lot, chosen explicitly — so it is not subject to it.
                if (!fromParcel && !IsEligible(entity, prefabEntity))
                {
                    LotPolicyState.ApprovedEntities.Add(entity);
                    continue;
                }

                var geom = em.GetComponentData<ObjectGeometryData>(prefabEntity);
                float buildingHeight = geom.m_Bounds.max.y;

                LotKey lot;
                if (fromParcel) lot = LotKey.ForParcel(parcel);
                else if (em.HasComponent<Game.Objects.Transform>(entity)) lot = LotKey.ForPosition(PositionKey(entity));
                else lot = LotKey.None;

                if (BuildingHeightLoader.IsBuildingAllowed(activeTiers, buildingHeight))
                {
                    // Satisfying building landed — enforce it and clear any give-up state for this lot.
                    LotPolicyState.ApprovedEntities.Add(entity);
                    ClearRerollState(lot);
                }
                else if (IsGivenUp(lot))
                {
                    // This lot's density can't produce a valid asset. Under KeepBuilding that
                    // means accepting whatever spawns; under DezonePlot the lot should already
                    // be unzoned, so a building reappearing here gets the same treatment again
                    // rather than being grandfathered in.
                    ApplyFallback(entity, ecb, buildingHeight, districtEntity);
                }
                else
                {
                    int count = BumpReroll(lot);

                    if (lot.Trackable && count > LotPolicyState.MaxRerolls)
                    {
                        // Given up: no valid asset exists for this lot's density. What happens
                        // next is the user's Fallback System setting.
                        MarkGivenUp(lot);
                        Mod.log.Debug(
                            $"[DistrictHeightPolicySystem] {DescribeLot(lot, districtEntity)} unsatisfiable for its policy");
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
                            $"[DistrictHeightPolicySystem] Marking building {entity.Index} (height {buildingHeight:F1}m) deleted — no active tier covers it on {DescribeLot(lot, districtEntity)}");
                    }
                }
            }

            entities.Dispose();
            prefabs.Dispose();
        }

        // Whether this building is one the height policy is allowed to act on.
        // Residential at any density, Commercial and Office at high density only.
        private bool IsEligible(Entity entity, Entity prefabEntity)
        {
            // Residential Mixed carries both ResidentialProperty and CommercialProperty. The
            // residential check comes first so those keep being judged the way they always
            // have been, rather than being re-routed through the density test.
            if (EntityManager.HasComponent<ResidentialProperty>(entity)) return true;

            return IsHighDensityZone(prefabEntity);
        }

        // A spawnable building points at the zone prefab it grew from, and CS2 names those
        // "<Region> <Category> <Density>" — "EU Commercial High", "NA Commercial Low",
        // "Office High", and the same pattern in every region pack (CN, FR, UK, JP, ...).
        //
        // Matching on "High" alone rather than on the region prefix is deliberate: it means
        // every region pack, including ones that ship after this build, is covered without a
        // code change. Anything we can't positively identify as high density is treated as
        // not eligible — getting this wrong in that direction would dezone lots the user
        // never wanted touched.
        private bool IsHighDensityZone(Entity prefabEntity)
        {
            var em = EntityManager;
            if (!em.HasComponent<SpawnableBuildingData>(prefabEntity)) return false;

            var zonePrefab = em.GetComponentData<SpawnableBuildingData>(prefabEntity).m_ZonePrefab;
            if (zonePrefab == Entity.Null) return false;

            if (LotPolicyState.HighDensityZoneCache.TryGetValue(zonePrefab, out var cached))
                return cached;

            bool isHighDensity = false;
            string zoneName = null;
            try
            {
                zoneName = m_PrefabSystem.GetPrefabName(zonePrefab);
                isHighDensity = zoneName != null
                    && zoneName.IndexOf("High", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (System.Exception e)
            {
                Mod.log.Warn($"[DistrictHeightPolicySystem] Could not read zone prefab name: {e.Message}");
            }

            LotPolicyState.HighDensityZoneCache[zonePrefab] = isHighDensity;
            // One line per zone prefab per session — this is the record of what the mod decided
            // to enforce on, and the first thing to check if a zone behaves unexpectedly.
            Mod.log.Info(
                $"[DistrictHeightPolicySystem] Zone '{zoneName ?? "<unknown>"}' classified as {(isHighDensity ? "high density — policy applies" : "not high density — skipped")}");

            return isHighDensity;
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

            // Never dezone a Platter parcel. DezoneLot strips the cells under the *building*, and a
            // building can be smaller than the parcel it grew on — so on a 6x6 parcel with a 4x4
            // building this carved out the 4x4 and left the game to re-block the remainder as
            // 2x4 + 4x4, permanently fragmenting a parcel the user placed by hand. Keeping the
            // wrong-height building is the lesser harm, and it is recoverable: the user can
            // bulldoze it or widen the policy. A fragmented parcel is not.
            //
            // Deliberately not gated on the PlatterIntegration setting — this prevents damage and
            // has no dependency on any Platter behaviour beyond the component's existence.
            if (m_ParcelReadable)
            {
                var parcel = GetParcel(entity);
                if (parcel != Entity.Null)
                {
                    LotPolicyState.ApprovedEntities.Add(entity);
                    Mod.log.Info(
                        $"[DistrictHeightPolicySystem] Keeping {buildingHeight:F1}m building {entity.Index} " +
                        $"on Platter parcel {parcel.Index} — dezoning would fragment the parcel");
                    return;
                }
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
