using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using DistrictHeightPolicy;

namespace DistrictMod.Data
{
    // A one-time scan of every level-1 growable building prefab: its zone, its lot size and its
    // height. Two consumers:
    //
    //   * the Height Restriction section in Platter's tool panel, which needs "how many buildings
    //     exist for this zone + this parcel size + this tier" so an impossible combination is
    //     visible before the parcel is placed rather than after it fails to grow;
    //   * Setting.CalibrateRanges, which derives the tier boundaries from the heights that
    //     actually exist in the installed asset set instead of the hardcoded ones in
    //     BuildingHeightData.json.
    //
    // The bucketing rules are taken from Platter's own P_BuildingCacheSystem so that our per-tier
    // counts sum to the total its GetBuildingCount reports: query BuildingData +
    // SpawnableBuildingData, keep m_Level == 1, read the zone from SpawnableBuildingData
    // .m_ZonePrefab's ZoneData.m_ZoneType.m_Index and the lot size from BuildingData.m_LotSize.
    //
    // The height — the one thing Platter does not track — comes from ObjectGeometryData
    // .m_Bounds.max.y, the same value DistrictHeightPolicySystem judges spawned buildings by, so
    // a count shown here cannot disagree with what enforcement will actually allow.
    public class BuildingHeightScan
    {
        // Set by PlatterHeightUISystem, which owns the instance. Settings code needs to reach the
        // scan for the calibrate button and has no route into the ECS world of its own.
        public static BuildingHeightScan Instance { get; private set; }

        private struct Rec
        {
            public ushort Zone;
            public float Height;
        }

        private const char kTierSep = ';';
        private const char kFieldSep = '~';

        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_Query;

        // Records grouped by lot size, which is how the UI always asks for them (Platter has one
        // parcel size selected at a time). Key is LotKey(w, d).
        private readonly Dictionary<int, List<Rec>> m_ByLotSize = new();

        // Distinct zones seen, for the summary log line only.
        private readonly HashSet<ushort> m_Zones = new();

        // Every (zone, width, depth) combination that has at least one building. Because the scan
        // uses Platter's own rules, this is exactly the key set of Platter's count cache, which is
        // what PlatterGridOverride walks instead of enumerating the native map by reflection.
        private readonly List<ZoneLot> m_Keys = new();

        private int m_Total;
        private bool m_Built;

        public struct ZoneLot
        {
            public ushort Zone;
            public int Width, Depth;
        }

        public BuildingHeightScan(World world)
        {
            m_EntityManager = world.EntityManager;

            m_Query = m_EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<BuildingData>(),
                    ComponentType.ReadOnly<SpawnableBuildingData>(),
                    ComponentType.ReadOnly<ObjectGeometryData>(),
                },
            });

            Instance = this;
        }

        public bool IsBuilt => m_Built;
        public int PrefabCount => m_Total;

        private static int LotKey(int w, int d) => (w << 8) | (d & 0xFF);

        // Prefabs are loaded once per session and the level-1 growable set does not change after
        // that, so this runs at most once and is a no-op thereafter. Tier ranges are deliberately
        // not baked in: counts are bucketed from the raw heights on request, so editing a range
        // in Options is reflected immediately without a rescan.
        public bool EnsureBuilt()
        {
            if (m_Built) return m_Total > 0;

            var entities = m_Query.ToEntityArray(Allocator.Temp);
            try
            {
                // Called from the UI tick, which can run before the prefab set exists. An empty
                // query is "not yet", not "none" — leave m_Built false and retry next tick.
                if (entities.Length == 0) return false;

                foreach (var prefab in entities)
                {
                    var spawnable = m_EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
                    if (spawnable.m_Level != 1) continue;
                    if (spawnable.m_ZonePrefab == Entity.Null) continue;
                    if (!m_EntityManager.HasComponent<ZoneData>(spawnable.m_ZonePrefab)) continue;

                    var zone = m_EntityManager.GetComponentData<ZoneData>(spawnable.m_ZonePrefab);
                    var building = m_EntityManager.GetComponentData<BuildingData>(prefab);
                    var geometry = m_EntityManager.GetComponentData<ObjectGeometryData>(prefab);

                    ushort zoneIndex = zone.m_ZoneType.m_Index;
                    int key = LotKey(building.m_LotSize.x, building.m_LotSize.y);

                    if (!m_ByLotSize.TryGetValue(key, out var list))
                        m_ByLotSize[key] = list = new List<Rec>();

                    if (!list.Exists(r => r.Zone == zoneIndex))
                        m_Keys.Add(new ZoneLot
                        {
                            Zone = zoneIndex,
                            Width = building.m_LotSize.x,
                            Depth = building.m_LotSize.y,
                        });

                    list.Add(new Rec { Zone = zoneIndex, Height = geometry.m_Bounds.max.y });
                    m_Zones.Add(zoneIndex);
                    m_Total++;
                }
            }
            finally
            {
                entities.Dispose();
            }

            m_Built = true;
            Mod.log.Info($"[BuildingHeightScan] {m_Total} level-1 growables across " +
                         $"{m_ByLotSize.Count} lot sizes and {m_Zones.Count} zones.");
            return m_Total > 0;
        }

        // Bounds match BuildingHeightLoader.IsBuildingAllowed exactly — exclusive min, inclusive
        // max. Any drift here would show a count for a building enforcement then rejects.
        private static bool InTier(float height, HeightTier tier)
        {
            return BuildingHeightLoader.TierRanges.TryGetValue(tier, out var range)
                && height > range.Min && height <= range.Max;
        }

        public IReadOnlyList<ZoneLot> Keys => m_Keys;

        // How many buildings exist for this zone and lot size that satisfy the restriction, where
        // an empty tier set means "no restriction" and yields the unfiltered total — the same
        // convention BuildingHeightLoader.IsBuildingAllowed uses, so the number shown always
        // matches what enforcement would permit.
        public int CountFor(ushort zoneIndex, int w, int d, ICollection<HeightTier> tiers)
        {
            if (!m_ByLotSize.TryGetValue(LotKey(w, d), out var recs)) return 0;

            int count = 0;
            bool unrestricted = tiers == null || tiers.Count == 0;
            foreach (var rec in recs)
            {
                if (rec.Zone != zoneIndex) continue;
                if (unrestricted) { count++; continue; }
                foreach (var tier in tiers)
                {
                    if (!InTier(rec.Height, tier)) continue;
                    count++;
                    break;
                }
            }
            return count;
        }

        // "Tier~count;Tier~count;..." — one entry per tier, in the order given, where count is the
        // number of buildings matching zoneIndex and the lot size exactly: the number that can
        // actually grow on this parcel under this tier. The panel only uses it to grey a tier
        // nothing can fill; the numbers themselves are shown in Platter's own grid.
        public string Serialize(ushort zoneIndex, int w, int d, HeightTier[] tiers)
        {
            if (!EnsureBuilt()) return string.Empty;

            if (!m_ByLotSize.TryGetValue(LotKey(w, d), out var recs))
                return string.Join(kTierSep.ToString(),
                    tiers.Select(t => t + kFieldSep.ToString() + "0"));

            var parts = new List<string>(tiers.Length);
            foreach (var tier in tiers)
            {
                int count = 0;
                foreach (var rec in recs)
                {
                    if (rec.Zone != zoneIndex) continue;
                    if (InTier(rec.Height, tier)) count++;
                }
                parts.Add(tier + kFieldSep.ToString() + count);
            }

            return string.Join(kTierSep.ToString(), parts);
        }

        // Derives tier boundaries from the heights that actually exist rather than from the
        // hardcoded defaults, which were picked before any of this data was reachable.
        //
        // Boundaries are placed at the midpoints of the widest gaps in the sorted list of
        // distinct heights. Growable heights are quantised by floor count, so the distribution is
        // a set of clusters separated by empty space: cutting in the middle of the widest gaps
        // means no tier boundary lands on or next to a real building height, and every tier gets
        // at least one asset. Small keeps its 0 floor and Skyscraper its 9999 "no ceiling"
        // sentinel — only the interior boundaries are computed.
        public bool TryCalibrate(HeightTier[] tiers, out Dictionary<HeightTier, HeightRange> ranges)
        {
            ranges = null;
            if (!EnsureBuilt() || tiers.Length < 2) return false;

            var heights = m_ByLotSize.Values
                .SelectMany(l => l)
                .Select(r => (float)System.Math.Round(r.Height, 1))
                .Where(h => h > 0f)
                .Distinct()
                .OrderBy(h => h)
                .ToList();

            int needed = tiers.Length - 1;
            if (heights.Count <= needed) return false;

            // Index i means "the gap between heights[i] and heights[i+1]".
            var cuts = Enumerable.Range(0, heights.Count - 1)
                .OrderByDescending(i => heights[i + 1] - heights[i])
                .Take(needed)
                .Select(i => (float)System.Math.Round((heights[i] + heights[i + 1]) * 0.5f, 1))
                .OrderBy(b => b)
                .ToList();

            ranges = new Dictionary<HeightTier, HeightRange>();
            float min = 0f;
            for (int t = 0; t < tiers.Length; t++)
            {
                float max = t < cuts.Count ? cuts[t] : 9999f;
                ranges[tiers[t]] = new HeightRange { Min = min, Max = max };
                min = max;
            }
            return true;
        }
    }
}
