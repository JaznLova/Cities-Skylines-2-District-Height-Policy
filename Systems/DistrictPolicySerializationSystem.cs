using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictMod.Harmony;
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
        protected override void OnUpdate()
        {
        }

        public void SetDefaults(Context context)
        {
            DistrictPolicyStore.DistrictTiers.Clear();
            ZoneSpawnPatch.ClearSessionState();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var tiers = DistrictPolicyStore.DistrictTiers;
            writer.Write(tiers.Count);
            foreach (var kvp in tiers)
            {
                writer.Write(kvp.Key);
                writer.Write(TiersToMask(kvp.Value));
            }
            Mod.log.Info($"[DistrictPolicySerializationSystem] Serialized {tiers.Count} district polic{(tiers.Count == 1 ? "y" : "ies")}.");
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            DistrictPolicyStore.DistrictTiers.Clear();

            reader.Read(out int count);
            for (int i = 0; i < count; i++)
            {
                reader.Read(out Entity entity);
                reader.Read(out int mask);
                DistrictPolicyStore.DistrictTiers[entity] = MaskToTiers(mask);
            }

            ZoneSpawnPatch.ClearSessionState();
            Mod.log.Info($"[DistrictPolicySerializationSystem] Deserialized {count} district polic{(count == 1 ? "y" : "ies")}.");
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
