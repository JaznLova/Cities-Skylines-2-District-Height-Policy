using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using DistrictMod.Data;
using DistrictMod.Systems;
using DistrictMod.UI;

namespace DistrictHeightPolicy
{
    public class Mod : IMod
    {
        // Logs to Logs/DistrictHeightPolicy.log rather than the shared Player.log.
        public static readonly ILog log = LogManager
            .GetLogger(nameof(DistrictHeightPolicy))
            .SetShowsErrorsInUI(false);

        // Directory the mod dll was loaded from — BuildingHeightData.json and
        // height-policy-panel.js sit next to it.
        public static string ModDirectory { get; private set; }

        private Setting m_Setting;
        private HarmonyLib.Harmony m_Harmony;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.effectivenessLevel = Level.Info;
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                ModDirectory = Path.GetDirectoryName(asset.path);
                log.Info($"Current mod asset at {asset.path}");
            }
            else
            {
                ModDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                log.Warn($"Mod asset not resolved, falling back to {ModDirectory}");
            }

            // Populates BuildingHeightLoader.DefaultTierRanges (used by Setting.ResetToDefaults)
            // and gives TierRanges an initial value before Setting.PushToRuntime() overwrites it
            // below with whatever was actually loaded from the settings file (or its defaults).
            LoadHeightData();

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));

            AssetDatabase.global.LoadSettings(nameof(DistrictHeightPolicy), m_Setting, new Setting(this));

            // m_Setting now holds either the values restored from disk or its plain C#
            // defaults — push them onto BuildingHeightLoader/ZoneSpawnPatch, the stores the
            // Harmony patches actually read from.
            m_Setting.PushToRuntime();

            m_Harmony = new HarmonyLib.Harmony(nameof(DistrictHeightPolicy));
            m_Harmony.PatchAll(Assembly.GetExecutingAssembly());
            log.Info("Harmony patches applied.");

            // UISystemBase.OnCreate runs after the city is loaded, so the panel injection
            // it performs no longer needs the frame delay the BepInEx build required.
            updateSystem.UpdateAt<DistrictPolicyUISystem>(SystemUpdatePhase.UIUpdate);
            log.Info("DistrictPolicyUISystem registered.");

            updateSystem.UpdateAt<DistrictPolicySerializationSystem>(SystemUpdatePhase.Serialize);
            log.Info("DistrictPolicySerializationSystem registered.");
        }

        private void LoadHeightData()
        {
            var dataPath = Path.Combine(ModDirectory, "BuildingHeightData.json");
            if (!File.Exists(dataPath))
            {
                log.Error($"BuildingHeightData.json not found at {dataPath}");
                return;
            }

            BuildingHeightLoader.Load(dataPath);
            log.Info("BuildingHeightData loaded.");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

            m_Harmony?.UnpatchAll(nameof(DistrictHeightPolicy));
            m_Harmony = null;

            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}
