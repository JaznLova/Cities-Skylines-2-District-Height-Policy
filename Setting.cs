using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;
using DistrictMod.Data;
using DistrictMod.Harmony;

namespace DistrictHeightPolicy
{
    [FileLocation(nameof(DistrictHeightPolicy))]
    [SettingsUIGroupOrder(kHeightRangeGroup, kBehaviorGroup)]
    [SettingsUIShowGroupName(kHeightRangeGroup, kBehaviorGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";

        public const string kHeightRangeGroup = "HeightRanges";
        public const string kBehaviorGroup = "Behavior";

        public Setting(IMod mod) : base(mod)
        {

        }

        // --- Height tier ranges (meters). These are the actual persisted values (plain
        // auto-properties, own backing fields) — NOT proxies into BuildingHeightLoader.
        // A property whose getter reads live off shared static state is indistinguishable
        // from the "default" Setting instance LoadSettings diffs against (same backing state),
        // so nothing ever gets written to the settings file. PushToRuntime() (called from
        // Mod.cs after LoadSettings, and from each setter below) is what applies these values
        // to BuildingHeightLoader/ZoneSpawnPatch for the patches to actually use. ---

        private float m_SmallMin = 0f, m_SmallMax = 24f;
        private float m_MediumMin = 24f, m_MediumMax = 32f;
        private float m_LargeMin = 32f, m_LargeMax = 52f;
        private float m_TallMin = 52f, m_TallMax = 68f;
        private float m_SuperTallMin = 68f, m_SuperTallMax = 115f;
        private float m_SkyscraperMin = 115f, m_SkyscraperMax = 9999f;
        private int m_MaxRerolls = 10;

        [SettingsUISlider(min = 0f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SmallMin { get => m_SmallMin; set { m_SmallMin = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SmallMax { get => m_SmallMax; set { m_SmallMax = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float MediumMin { get => m_MediumMin; set { m_MediumMin = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float MediumMax { get => m_MediumMax; set { m_MediumMax = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float LargeMin { get => m_LargeMin; set { m_LargeMin = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float LargeMax { get => m_LargeMax; set { m_LargeMax = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float TallMin { get => m_TallMin; set { m_TallMin = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float TallMax { get => m_TallMax; set { m_TallMax = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SuperTallMin { get => m_SuperTallMin; set { m_SuperTallMin = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SuperTallMax { get => m_SuperTallMax; set { m_SuperTallMax = value; PushToRuntime(); } }

        [SettingsUISlider(min = -1f, max = 300f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SkyscraperMin { get => m_SkyscraperMin; set { m_SkyscraperMin = value; PushToRuntime(); } }

        // Skyscraper's default upper bound (9999) is a "no ceiling" sentinel, not a real
        // building height, so its slider goes well past the others' 300m cap.
        [SettingsUISlider(min = -1f, max = 9999f, step = 2f, scalarMultiplier = 1, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public float SkyscraperMax { get => m_SkyscraperMax; set { m_SkyscraperMax = value; PushToRuntime(); } }

        // --- Reroll behavior ---

        [SettingsUISlider(min = 1, max = 25, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kBehaviorGroup)]
        public int MaxRerolls { get => m_MaxRerolls; set { m_MaxRerolls = value; PushToRuntime(); } }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool ResetToDefaults
        {
            set
            {
                Mod.log.Info("Resetting height ranges and reroll count to defaults");
                var defaults = BuildingHeightLoader.DefaultTierRanges;
                if (defaults.TryGetValue(HeightTier.Small, out var small)) { m_SmallMin = small.Min; m_SmallMax = small.Max; }
                if (defaults.TryGetValue(HeightTier.Medium, out var medium)) { m_MediumMin = medium.Min; m_MediumMax = medium.Max; }
                if (defaults.TryGetValue(HeightTier.Large, out var large)) { m_LargeMin = large.Min; m_LargeMax = large.Max; }
                if (defaults.TryGetValue(HeightTier.Tall, out var tall)) { m_TallMin = tall.Min; m_TallMax = tall.Max; }
                if (defaults.TryGetValue(HeightTier.SuperTall, out var superTall)) { m_SuperTallMin = superTall.Min; m_SuperTallMax = superTall.Max; }
                if (defaults.TryGetValue(HeightTier.Skyscraper, out var skyscraper)) { m_SkyscraperMin = skyscraper.Min; m_SkyscraperMax = skyscraper.Max; }
                m_MaxRerolls = 10;
                PushToRuntime();
            }
        }

        // Applies the persisted values above onto the runtime stores the patches actually
        // read from. Called once after settings load (Mod.cs) and again on every edit here.
        public void PushToRuntime()
        {
            BuildingHeightLoader.SetRange(HeightTier.Small, m_SmallMin, m_SmallMax);
            BuildingHeightLoader.SetRange(HeightTier.Medium, m_MediumMin, m_MediumMax);
            BuildingHeightLoader.SetRange(HeightTier.Large, m_LargeMin, m_LargeMax);
            BuildingHeightLoader.SetRange(HeightTier.Tall, m_TallMin, m_TallMax);
            BuildingHeightLoader.SetRange(HeightTier.SuperTall, m_SuperTallMin, m_SuperTallMax);
            BuildingHeightLoader.SetRange(HeightTier.Skyscraper, m_SkyscraperMin, m_SkyscraperMax);
            ZoneSpawnPatch.MaxRerolls = m_MaxRerolls;
            ZoneSpawnPatch.ResetLotState();
        }

        public override void SetDefaults()
        {
            m_SmallMin = 0f; m_SmallMax = 24f;
            m_MediumMin = 24f; m_MediumMax = 32f;
            m_LargeMin = 32f; m_LargeMax = 52f;
            m_TallMin = 52f; m_TallMax = 68f;
            m_SuperTallMin = 68f; m_SuperTallMax = 115f;
            m_SkyscraperMin = 115f; m_SkyscraperMax = 9999f;
            m_MaxRerolls = 10;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;
        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "DistrictHeightPolicy" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(Setting.kHeightRangeGroup), "Height Ranges" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBehaviorGroup), "Behavior" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SmallMin)), "Small: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SmallMax)), "Small: max height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MediumMin)), "Medium: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MediumMax)), "Medium: max height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LargeMin)), "Large: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LargeMax)), "Large: max height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TallMin)), "Tall: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TallMax)), "Tall: max height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SuperTallMin)), "Super Tall: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SuperTallMax)), "Super Tall: max height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SkyscraperMin)), "Skyscraper: min height (m)" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SkyscraperMax)), "Skyscraper: max height (m)" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxRerolls)), "Max rerolls before auto-pick" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxRerolls)), "Number of times a lot's spawn is rejected and rerolled before the mod gives up and keeps whatever building spawned" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetToDefaults)), "Reset to defaults" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetToDefaults)), "Restore the height ranges and max reroll count to their original values" },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.ResetToDefaults)), "This will overwrite your custom height ranges and reroll count. Continue?" },
            };
        }

        public void Unload()
        {

        }
    }
}
