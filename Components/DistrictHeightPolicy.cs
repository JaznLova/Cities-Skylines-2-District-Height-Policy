using System.Collections.Generic;
using DistrictMod.Data;
using Unity.Entities;

namespace DistrictMod.Components
{
    // Key: district Entity (Index+Version, not just Index — Index alone is reused across
    // save/load and would otherwise silently point at whatever district gets that slot next)
    // Value: set of active height tiers. Empty set = no restriction (all buildings allowed).
    public static class DistrictPolicyStore
    {
        public static readonly Dictionary<Entity, HashSet<HeightTier>> DistrictTiers = new();

        public static HashSet<HeightTier> GetTiers(Entity districtEntity)
        {
            return DistrictTiers.TryGetValue(districtEntity, out var tiers) ? tiers : null;
        }

        public static void SetTiers(Entity districtEntity, HashSet<HeightTier> tiers)
        {
            DistrictTiers[districtEntity] = tiers;
        }

        public static void ToggleTier(Entity districtEntity, HeightTier tier)
        {
            if (!DistrictTiers.TryGetValue(districtEntity, out var tiers))
            {
                tiers = new HashSet<HeightTier>();
                DistrictTiers[districtEntity] = tiers;
            }
            if (!tiers.Add(tier))
                tiers.Remove(tier);
        }

        public static bool HasAnyPolicy(Entity districtEntity)
        {
            return DistrictTiers.TryGetValue(districtEntity, out var tiers) && tiers.Count > 0;
        }
    }
}
