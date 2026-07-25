using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;
using System.Linq;
using DistrictMod.Components;
using DistrictMod.Data;

namespace DistrictHeightPolicy
{
    [FileLocation(nameof(DistrictHeightPolicy))]
    [SettingsUIGroupOrder(kHeightRangeGroup, kBehaviorGroup, kFallbackGroup, kPlatterGroup)]
    [SettingsUIShowGroupName(kHeightRangeGroup, kBehaviorGroup, kFallbackGroup, kPlatterGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";

        public const string kHeightRangeGroup = "HeightRanges";
        public const string kBehaviorGroup = "Behavior";
        public const string kFallbackGroup = "Fallback";
        public const string kPlatterGroup = "Platter";

        public Setting(IMod mod) : base(mod)
        {

        }

        // --- Height tier ranges (meters). These are the actual persisted values (plain
        // auto-properties, own backing fields) — NOT proxies into BuildingHeightLoader.
        // A property whose getter reads live off shared static state is indistinguishable
        // from the "default" Setting instance LoadSettings diffs against (same backing state),
        // so nothing ever gets written to the settings file. PushToRuntime() (called from
        // Mod.cs after LoadSettings, and from each setter below) is what applies these values
        // to BuildingHeightLoader/LotPolicyState for the runtime to actually use. ---

        private float m_SmallMin = 0f, m_SmallMax = 24f;
        private float m_MediumMin = 24f, m_MediumMax = 32f;
        private float m_LargeMin = 32f, m_LargeMax = 52f;
        private float m_TallMin = 52f, m_TallMax = 68f;
        private float m_SuperTallMin = 68f, m_SuperTallMax = 115f;
        private float m_SkyscraperMin = 115f, m_SkyscraperMax = 9999f;
        private int m_MaxRerolls = 10;
        private FallbackMode m_Fallback = FallbackMode.DezonePlot;
        private bool m_EnablePlatterIntegration = true;

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

        // --- Fallback system: what happens once a lot has burned through its rerolls ---
        // An enum-typed setting renders as a dropdown on its own; the entries come from the
        // GetEnumValueLocaleID(...) keys in LocaleEN below.

        [SettingsUISection(kSection, kFallbackGroup)]
        public FallbackMode Fallback { get => m_Fallback; set { m_Fallback = value; PushToRuntime(); } }

        // Display-only line under the dropdown. It carries no value of its own — the text is
        // the locale entry for this property's label — and only shows in Dezone Plot mode.
        [SettingsUIMultilineText]
        [SettingsUISection(kSection, kFallbackGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotDezoneMode))]
        public string DezoneNote => string.Empty;

        public bool IsNotDezoneMode() => m_Fallback != FallbackMode.DezonePlot;

        // --- Platter soft dependency ---
        // Off switch for the whole integration. Platter is not required for this mod, and its
        // panel is not ours: if a Platter update moves the section this hooks onto, turning
        // this off removes our UI from Platter entirely and leaves district enforcement alone.

        [SettingsUISection(kSection, kPlatterGroup)]
        public bool EnablePlatterIntegration
        {
            get => m_EnablePlatterIntegration;
            set { m_EnablePlatterIntegration = value; PushToRuntime(); }
        }

        // Replaces the hardcoded boundaries with ones derived from the heights that actually exist
        // in the installed asset set — see BuildingHeightScan.TryCalibrate for how the cuts are
        // chosen. This cannot be the plain default: prefabs do not exist yet when Mod.OnLoad runs
        // and the settings file is read, so there is nothing to measure until a city is loaded.
        // Hence a button, which also keeps the JSON values available as "reset to defaults".
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kHeightRangeGroup)]
        public bool CalibrateRanges
        {
            set
            {
                var scan = BuildingHeightScan.Instance;
                if (scan == null || !scan.TryCalibrate(BuildingHeightLoader.AllTiers, out var ranges))
                {
                    Mod.log.Warn("Calibration needs a loaded city to read building heights from — " +
                                 "load a save and try again. Ranges left unchanged.");
                    return;
                }

                if (ranges.TryGetValue(HeightTier.Small, out var small)) { m_SmallMin = small.Min; m_SmallMax = small.Max; }
                if (ranges.TryGetValue(HeightTier.Medium, out var medium)) { m_MediumMin = medium.Min; m_MediumMax = medium.Max; }
                if (ranges.TryGetValue(HeightTier.Large, out var large)) { m_LargeMin = large.Min; m_LargeMax = large.Max; }
                if (ranges.TryGetValue(HeightTier.Tall, out var tall)) { m_TallMin = tall.Min; m_TallMax = tall.Max; }
                if (ranges.TryGetValue(HeightTier.SuperTall, out var superTall)) { m_SuperTallMin = superTall.Min; m_SuperTallMax = superTall.Max; }
                if (ranges.TryGetValue(HeightTier.Skyscraper, out var skyscraper)) { m_SkyscraperMin = skyscraper.Min; m_SkyscraperMax = skyscraper.Max; }

                Mod.log.Info($"Calibrated height ranges from {scan.PrefabCount} building prefabs: " +
                             string.Join(", ", BuildingHeightLoader.AllTiers
                                 .Select(t => $"{t} {ranges[t].Min}-{ranges[t].Max}")));
                PushToRuntime();
            }
        }

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
                m_Fallback = FallbackMode.DezonePlot;
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
            LotPolicyState.MaxRerolls = m_MaxRerolls;
            LotPolicyState.Fallback = m_Fallback;
            LotPolicyState.PlatterIntegration = m_EnablePlatterIntegration;
            LotPolicyState.ResetLotState();
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
            m_Fallback = FallbackMode.DezonePlot;
            m_EnablePlatterIntegration = true;
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kFallbackGroup), "Fallback System" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlatterGroup), "Platter Integration" },

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

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Fallback)), "Fallback system" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Fallback)), "What happens to a lot once it has used up its rerolls and still cannot produce a building the district's policy allows" },
                { m_Setting.GetEnumValueLocaleID(FallbackMode.DezonePlot), "Dezone Plot" },
                { m_Setting.GetEnumValueLocaleID(FallbackMode.KeepBuilding), "Keep Building" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DezoneNote)), "Zone will be removed if no building exists for select policy or plot." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnablePlatterIntegration)), "Enable Platter integration" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EnablePlatterIntegration)), "Adds a Height Restriction section to Platter's tool panel so a parcel can carry its own height policy. Turn this off if a Platter update breaks the section — district height policies are unaffected either way." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrateRanges)), "Calibrate from installed assets" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrateRanges)), "Recalculates the six height ranges from the actual heights of the growable buildings you have installed, placing each boundary in a gap where no building sits. Requires a loaded city." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.CalibrateRanges)), "This will overwrite your current height ranges with values measured from your installed assets. Continue?" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetToDefaults)), "Reset to defaults" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetToDefaults)), "Restore the height ranges, max reroll count and fallback system to their original values" },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.ResetToDefaults)), "This will overwrite your custom height ranges, reroll count and fallback system. Continue?" },
            };
        }

        public void Unload()
        {

        }
    }
}
