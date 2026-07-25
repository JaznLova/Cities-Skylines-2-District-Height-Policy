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
        private const int kVersion = 1;             // 1: adds the parcel override block

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

            Mod.log.Info($"[DistrictPolicySerializationSystem] Serialized {tiers.Count} district " +
                         $"polic{(tiers.Count == 1 ? "y" : "ies")} and {overrides.Count} parcel override(s).");
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

            // Clears the session state, including the parcel seen-set — but not the overrides just
            // read above, which is why they live in their own store. See ParcelOverrideStore.
            LotPolicyState.ClearSessionState();
            Mod.log.Info($"[DistrictPolicySerializationSystem] Deserialized v{version}: {count} district " +
                         $"polic{(count == 1 ? "y" : "ies")}, {parcelCount} parcel override(s).");
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
