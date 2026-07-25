using System.Collections.Generic;
using Unity.Entities;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.Components
{
    // Height restrictions chosen per Platter parcel, which override the policy of whatever district
    // the parcel sits in — and apply even when it sits in none.
    //
    // Key: the parcel Entity. Not a quantized world position, which is what an earlier plan for this
    // called for: the override is written from the parcel and read from the building that grows on
    // it, and a building can be smaller than its parcel (Platter itself ships a string saying so),
    // so the two transforms need not agree and a position join would silently never match. Platter's
    // LinkedParcel gives the building's parcel exactly, and Entity keys survive a load because they
    // are written through IWriter.Write(Entity) and remapped, the same as district keys already are.
    //
    // Deliberately NOT a member of LotPolicyState. DistrictPolicySerializationSystem.Deserialize
    // fills this and then calls LotPolicyState.ClearSessionState(), so living there would wipe every
    // override on every load. Keeping it in a separate type makes that mistake impossible instead of
    // merely documented.
    public static class ParcelOverrideStore
    {
        // Value: the tiers a building on this parcel may be. Never empty — an empty selection means
        // "no restriction", which is recorded by storing nothing at all. See SnapshotPending.
        public static readonly Dictionary<Entity, HashSet<HeightTier>> ParcelTiers = new();

        public static int Count => ParcelTiers.Count;

        public static HashSet<HeightTier> GetTiers(Entity parcel)
        {
            return ParcelTiers.TryGetValue(parcel, out var tiers) ? tiers : null;
        }

        public static bool Has(Entity parcel) => ParcelTiers.ContainsKey(parcel);

        public static void SetTiers(Entity parcel, HashSet<HeightTier> tiers)
        {
            ParcelTiers[parcel] = tiers;
        }

        // The restriction to stamp a newly placed parcel with, or null for "none — let the district
        // decide". Both extremes of the panel mean no restriction: nothing ticked is obviously not a
        // restriction, and every tier ticked permits every height, so recording it would only serve
        // to exempt the parcel from its district. Since all-ticked is the state a user is most
        // likely to leave the panel in without meaning anything by it, that would silently disable
        // district policy across every parcel they place.
        public static IReadOnlyCollection<HeightTier> SnapshotPending()
        {
            var pending = LotPolicyState.PlatterPendingTiers;
            int n = pending.Count;
            return n >= 1 && n < BuildingHeightLoader.AllTiers.Length ? pending : null;
        }

        // Drops overrides whose parcel no longer exists — bulldozed, or removed by a Platter
        // update. Tests Exists only, never HasComponent: with Platter uninstalled the entity may
        // survive without its Parcel component, and discarding the override then would destroy
        // data the user gets back for free by reinstalling.
        public static int PruneDestroyed(EntityManager em)
        {
            if (ParcelTiers.Count == 0) return 0;

            List<Entity> dead = null;
            foreach (var kvp in ParcelTiers)
            {
                if (em.Exists(kvp.Key)) continue;
                (dead ??= new List<Entity>()).Add(kvp.Key);
            }

            if (dead == null) return 0;
            foreach (var parcel in dead) ParcelTiers.Remove(parcel);

            Mod.log.Info($"[ParcelOverrideStore] Dropped {dead.Count} override(s) for parcels that " +
                         "no longer exist.");
            return dead.Count;
        }
    }
}
