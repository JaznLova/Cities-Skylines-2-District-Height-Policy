using System.Collections.Generic;
using System.IO;

namespace DistrictMod.Data
{
    public enum HeightTier { None = 0, Small, Medium, Large, Tall, SuperTall, Skyscraper }

    public struct HeightRange
    {
        public float Min;
        public float Max;
    }

    public static class BuildingHeightLoader
    {
        public static Dictionary<HeightTier, HeightRange> TierRanges { get; private set; }
            = new Dictionary<HeightTier, HeightRange>();

        // Ranges as originally read from BuildingHeightData.json — used to restore
        // TierRanges when the user hits "Reset to defaults" in the mod settings.
        public static IReadOnlyDictionary<HeightTier, HeightRange> DefaultTierRanges { get; private set; }
            = new Dictionary<HeightTier, HeightRange>();

        public static void Load(string jsonPath)
        {
            var json = File.ReadAllText(jsonPath);
            var result = new Dictionary<HeightTier, HeightRange>();

            int i = 0;
            SkipWhitespace(json, ref i);
            Expect(json, ref i, '{');

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] == '}') break;
                if (json[i] == ',') { i++; continue; }

                var key = ReadString(json, ref i);
                SkipWhitespace(json, ref i);
                Expect(json, ref i, ':');
                SkipWhitespace(json, ref i);
                var range = ReadRange(json, ref i);

                if (System.Enum.TryParse<HeightTier>(key, out var tier))
                    result[tier] = range;
            }

            TierRanges = result;
            DefaultTierRanges = new Dictionary<HeightTier, HeightRange>(result);
        }

        public static void SetRange(HeightTier tier, float min, float max)
        {
            TierRanges[tier] = new HeightRange { Min = min, Max = max };
        }

        public static void ResetToDefaults()
        {
            TierRanges = new Dictionary<HeightTier, HeightRange>(DefaultTierRanges);
        }

        // Returns true if the building is allowed given the set of active tiers.
        // A building is allowed if its height falls within ANY of the active tiers' ranges.
        public static bool IsBuildingAllowed(IReadOnlyCollection<HeightTier> activeTiers, float buildingHeight)
        {
            if (activeTiers == null || activeTiers.Count == 0) return true;
            foreach (var tier in activeTiers)
            {
                if (!TierRanges.TryGetValue(tier, out var range)) continue;
                if (buildingHeight > range.Min && buildingHeight <= range.Max) return true;
            }
            return false;
        }

        private static HeightRange ReadRange(string s, ref int i)
        {
            var range = new HeightRange();
            Expect(s, ref i, '{');
            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }

                var key = ReadString(s, ref i);
                SkipWhitespace(s, ref i);
                Expect(s, ref i, ':');
                SkipWhitespace(s, ref i);
                var val = ReadNumber(s, ref i);

                if (key == "min") range.Min = val;
                else if (key == "max") range.Max = val;
            }
            return range;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
                i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i < s.Length && s[i] == c) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            int start = i;
            while (i < s.Length && s[i] != '"') i++;
            var result = s.Substring(start, i - start);
            i++;
            return result;
        }

        private static float ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-'))
                i++;
            float.TryParse(s.Substring(start, i - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float val);
            return val;
        }
    }
}
