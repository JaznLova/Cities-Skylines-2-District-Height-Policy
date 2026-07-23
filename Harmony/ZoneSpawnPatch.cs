using System.Collections.Generic;
using HarmonyLib;
using Game.Simulation;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictMod.Systems;
using DistrictHeightPolicy;

namespace DistrictMod.Harmony
{
    [HarmonyPatch(typeof(ZoneSpawnSystem), "OnUpdate")]
    public static class ZoneSpawnPatch
    {
        private static readonly HashSet<int> _approvedEntities = new();
        private static readonly HashSet<Entity> _activatedDistricts = new();

        // Per-lot reroll tracking (keyed by quantized world position, stable across respawns).
        private static readonly Dictionary<long, int> _rerollCounts = new();
        private static readonly HashSet<long> _unsatisfiableLots = new();

        // Rerolls allowed before a lot is given up on and its spawned building kept as-is.
        // Exposed via Setting.MaxRerolls (1-25); defaults to the original hardcoded value.
        public static int MaxRerolls { get; set; } = 10;

        private static int _tickCount = 0;

        // Called when a district policy changes so lots are re-evaluated rather than
        // staying frozen as "unsatisfiable".
        public static void ResetLotState()
        {
            _rerollCounts.Clear();
            _unsatisfiableLots.Clear();
        }

        // Called whenever a save is loaded (or a new city starts): all of this state is
        // keyed by per-session entity data, so it must never carry over across a reload —
        // a reused Entity.Index/Version slot from a previous session could otherwise let a
        // stale "already approved"/"already grandfathered" decision apply to a different
        // district or building.
        public static void ClearSessionState()
        {
            _approvedEntities.Clear();
            _activatedDistricts.Clear();
            ResetLotState();
        }

        private static long PositionKey(EntityManager em, Entity entity)
        {
            var pos = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            long x = (long)System.Math.Round((double)pos.x);
            long z = (long)System.Math.Round((double)pos.z);
            return (x << 20) ^ (z & 0xFFFFF);
        }

        [HarmonyPostfix]
        public static void Postfix(ZoneSpawnSystem __instance)
        {
            _tickCount++;
            if (_tickCount <= 3)
                Mod.log.Info($"[ZoneSpawnPatch] Postfix tick {_tickCount}");

            var em = __instance.EntityManager;

            if (DistrictPolicyStore.DistrictTiers.Count == 0) return;

            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.ReadOnly<CurrentDistrict>(),
                ComponentType.ReadOnly<PrefabRef>()
            );

            var entities  = query.ToEntityArray(Allocator.Temp);
            var districts = query.ToComponentDataArray<CurrentDistrict>(Allocator.Temp);
            var prefabs   = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            query.Dispose();

            // Grandfather all pre-existing buildings when a district first gets a policy.
            foreach (var kvp in DistrictPolicyStore.DistrictTiers)
            {
                if (kvp.Value.Count == 0) continue;
                if (_activatedDistricts.Contains(kvp.Key)) continue;

                _activatedDistricts.Add(kvp.Key);
                for (int i = 0; i < entities.Length; i++)
                {
                    if (districts[i].m_District == kvp.Key)
                        _approvedEntities.Add(entities[i].Index);
                }
                Mod.log.Info($"[ZoneSpawnPatch] Grandfathered existing buildings in district {kvp.Key.Index}");
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (_approvedEntities.Contains(entity.Index)) continue;

                var districtEntity = districts[i].m_District;
                var activeTiers = DistrictPolicyStore.GetTiers(districtEntity);

                if (activeTiers == null || activeTiers.Count == 0)
                {
                    _approvedEntities.Add(entity.Index);
                    continue;
                }

                var prefabEntity = prefabs[i].m_Prefab;
                if (!em.HasComponent<ObjectGeometryData>(prefabEntity))
                {
                    _approvedEntities.Add(entity.Index);
                    continue;
                }

                var geom = em.GetComponentData<ObjectGeometryData>(prefabEntity);
                float buildingHeight = geom.m_Bounds.max.y;

                bool hasTransform = em.HasComponent<Game.Objects.Transform>(entity);
                long posKey = hasTransform ? PositionKey(em, entity) : 0;

                if (BuildingHeightLoader.IsBuildingAllowed(activeTiers, buildingHeight))
                {
                    // Satisfying building landed — enforce it and clear any give-up state for this lot.
                    _approvedEntities.Add(entity.Index);
                    if (hasTransform)
                    {
                        _rerollCounts.Remove(posKey);
                        _unsatisfiableLots.Remove(posKey);
                    }
                }
                else if (hasTransform && _unsatisfiableLots.Contains(posKey))
                {
                    // This lot's density can't produce a valid asset — keep whatever spawns.
                    _approvedEntities.Add(entity.Index);
                }
                else
                {
                    int count = hasTransform ? (_rerollCounts.TryGetValue(posKey, out var c) ? c : 0) + 1 : 0;
                    if (hasTransform) _rerollCounts[posKey] = count;

                    if (hasTransform && count > MaxRerolls)
                    {
                        // Given up: no valid asset exists for this lot's density. Keep the building.
                        _unsatisfiableLots.Add(posKey);
                        _approvedEntities.Add(entity.Index);
                        Mod.log.Info(
                            $"[ZoneSpawnPatch] Lot at {posKey} unsatisfiable for policy — keeping {buildingHeight:F1}m building in district {districtEntity.Index}");
                    }
                    else
                    {
                        ecb.AddComponent<Deleted>(entity);
                        Mod.log.Info(
                            $"[ZoneSpawnPatch] Marking building {entity.Index} (height {buildingHeight:F1}m) deleted — no active tier covers it in district {districtEntity.Index}");
                    }
                }
            }

            ecb.Playback(em);
            ecb.Dispose();
            entities.Dispose();
            districts.Dispose();
            prefabs.Dispose();
        }
    }
}
