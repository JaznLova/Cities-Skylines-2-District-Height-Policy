using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Persists DistrictPolicyStore.DistrictTiers as part of the city save itself (not a
    // mod-folder JSON file — this data is per-save, not global like BuildingHeightData.json).
    // Entities are written/read directly rather than as raw Entity.Index, so district
    // identity survives the entity remapping that happens whenever a save is loaded.
    // IDefaultSerializable (rather than plain ISerializable) avoids the game's own
    // "should use IDefaultSerializable/IJobSerializable instead" log warning, and gives us
    // SetDefaults — called for a brand new city, where Deserialize never runs.
    public partial class DistrictPolicySerializationSystem : GameSystemBase, IDefaultSerializable
    {
        // The original format had no header and began with a district count, which can never be
        // negative — so a negative first int is a safe marker for "this save has a header". A
        // reader seeing a non-negative first int is looking at a save written before this change
        // and reads it the old way.
        //
        // This is deliberately one-way: an older build of this mod reading a new save would take
        // kMagic for a count of zero and lose every district policy. Downgrading is lossy.
        private const int kMagic = -0x44485001;
        // 1: adds the parcel override block
        // 2: adds the parcel reroll state, so a parcel already given up on is not re-judged from
        //    scratch on every load
        private const int kVersion = 2;

        protected override void OnUpdate()
        {
        }

        public void SetDefaults(Context context)
        {
            DistrictPolicyStore.DistrictTiers.Clear();
            ParcelOverrideStore.ParcelTiers.Clear();
            LotPolicyState.ClearSessionState();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kMagic);
            writer.Write(kVersion);

            var tiers = DistrictPolicyStore.DistrictTiers;
            writer.Write(tiers.Count);
            foreach (var kvp in tiers)
            {
                writer.Write(kvp.Key);
                writer.Write(TiersToMask(kvp.Value));
            }

            // Written unconditionally — not gated on EnablePlatterIntegration, and not on Platter
            // being installed. Either gate would silently strip every override out of the user's
            // save after one session with the setting off or the mod temporarily removed.
            var overrides = ParcelOverrideStore.ParcelTiers;
            writer.Write(overrides.Count);
            foreach (var kvp in overrides)
            {
                writer.Write(kvp.Key);
                writer.Write(TiersToMask(kvp.Value));
            }

            // How far each overridden parcel got through its rerolls, and which parcels were given
            // up on entirely. Session state everywhere else, but it has to survive a load here:
            // buildings on a district-policied lot are re-approved wholesale by the grandfathering
            // pass after a load, and parcels deliberately skip that pass — so without this a parcel
            // that had already been settled starts its reroll loop again on every single load,
            // deleting the building MaxRerolls times before landing back where it was.
            var givenUp = LotPolicyState.UnsatisfiableParcels;
            writer.Write(givenUp.Count);
            foreach (var parcel in givenUp) writer.Write(parcel);

            var rerolls = LotPolicyState.ParcelRerollCounts;
            writer.Write(rerolls.Count);
            foreach (var kvp in rerolls)
            {
                writer.Write(kvp.Key);
                writer.Write(kvp.Value);
            }

            Mod.log.Info($"[DistrictPolicySerializationSystem] Serialized {tiers.Count} district " +
                         $"polic{(tiers.Count == 1 ? "y" : "ies")}, {overrides.Count} parcel override(s), " +
                         $"{givenUp.Count} settled parcel(s).");
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            DistrictPolicyStore.DistrictTiers.Clear();
            ParcelOverrideStore.ParcelTiers.Clear();

            reader.Read(out int first);

            int version = 0;
            int count;
            if (first < 0)
            {
                if (first != kMagic)
                    Mod.log.Warn($"[DistrictPolicySerializationSystem] Unexpected header {first} — " +
                                 "reading as a versioned save anyway.");
                reader.Read(out version);
                reader.Read(out count);
            }
            else
            {
                count = first;              // pre-header save: the first int really is the count
            }

            for (int i = 0; i < count; i++)
            {
                reader.Read(out Entity entity);
                reader.Read(out int mask);
                DistrictPolicyStore.DistrictTiers[entity] = MaskToTiers(mask);
            }

            int parcelCount = 0;
            if (version >= 1)
            {
                reader.Read(out parcelCount);
                for (int i = 0; i < parcelCount; i++)
                {
                    reader.Read(out Entity parcel);
                    reader.Read(out int mask);

                    // Entities go through IWriter.Write(Entity), so the loader remaps them exactly
                    // as it already does for districts. Entity.Null is the only value that cannot
                    // name a live parcel. Nothing else is validated here on purpose: a session
                    // without Platter installed must not discard the block. Parcels that really
                    // are gone are dropped by ParcelOverrideStore.PruneDestroyed, which only runs
                    // when Platter is present.
                    if (parcel != Entity.Null)
                        ParcelOverrideStore.ParcelTiers[parcel] = MaskToTiers(mask);
                }
            }

            // Read into locals, not straight into LotPolicyState: ClearSessionState below wipes
            // both of these, so they have to be applied after it rather than before.
            List<Entity> givenUp = null;
            List<KeyValuePair<Entity, int>> rerolls = null;
            if (version >= 2)
            {
                reader.Read(out int givenUpCount);
                givenUp = new List<Entity>(givenUpCount);
                for (int i = 0; i < givenUpCount; i++)
                {
                    reader.Read(out Entity parcel);
                    if (parcel != Entity.Null) givenUp.Add(parcel);
                }

                reader.Read(out int rerollCount);
                rerolls = new List<KeyValuePair<Entity, int>>(rerollCount);
                for (int i = 0; i < rerollCount; i++)
                {
                    reader.Read(out Entity parcel);
                    reader.Read(out int attempts);
                    if (parcel != Entity.Null)
                        rerolls.Add(new KeyValuePair<Entity, int>(parcel, attempts));
                }
            }

            // Clears the session state, including the parcel seen-set — but not the overrides just
            // read above, which is why they live in their own store. See ParcelOverrideStore.
            LotPolicyState.ClearSessionState();

            if (givenUp != null)
            {
                foreach (var parcel in givenUp) LotPolicyState.UnsatisfiableParcels.Add(parcel);
                foreach (var kvp in rerolls) LotPolicyState.ParcelRerollCounts[kvp.Key] = kvp.Value;
            }

            Mod.log.Info($"[DistrictPolicySerializationSystem] Deserialized v{version}: {count} district " +
                         $"polic{(count == 1 ? "y" : "ies")}, {parcelCount} parcel override(s), " +
                         $"{givenUp?.Count ?? 0} settled parcel(s).");
        }

        private static int TiersToMask(HashSet<HeightTier> tiers)
        {
            int mask = 0;
            foreach (var tier in tiers)
                mask |= 1 << (int)tier;
            return mask;
        }

        private static HashSet<HeightTier> MaskToTiers(int mask)
        {
            var tiers = new HashSet<HeightTier>();
            foreach (HeightTier tier in System.Enum.GetValues(typeof(HeightTier)))
            {
                if (tier == HeightTier.None) continue;
                if ((mask & (1 << (int)tier)) != 0)
                    tiers.Add(tier);
            }
            return tiers;
        }
    }
}
