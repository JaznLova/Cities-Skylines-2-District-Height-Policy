using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.UI;
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

        // The panel is injected into the game's cohtml view, which can't read arbitrary
        // filesystem paths. Registering this host makes icons/*.png reachable from the
        // injected script as coui://districtheightpolicy/<file>.
        private const string kUIHostName = "districtheightpolicy";
        private bool m_UIHostRegistered;

        private Setting m_Setting;

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
            RegisterUIHost();

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));

            AssetDatabase.global.LoadSettings(nameof(DistrictHeightPolicy), m_Setting, new Setting(this));

            // m_Setting now holds either the values restored from disk or its plain C#
            // defaults — push them onto BuildingHeightLoader/LotPolicyState, the stores the
            // enforcement system actually reads from.
            m_Setting.PushToRuntime();

            // UISystemBase.OnCreate runs after the city is loaded, so the panel injection
            // it performs no longer needs the frame delay the BepInEx build required.
            updateSystem.UpdateAt<DistrictPolicyUISystem>(SystemUpdatePhase.UIUpdate);
            log.Info("DistrictPolicyUISystem registered.");

            // Platter soft dependency: adds a Height Restriction section to Platter's tool
            // panel. Registered unconditionally — the script no-ops until a Platter panel is on
            // screen, so it costs nothing when Platter is not installed, and the
            // EnablePlatterIntegration setting is checked per tick rather than here so it can
            // be switched off mid-session.
            updateSystem.UpdateAt<PlatterHeightUISystem>(SystemUpdatePhase.UIUpdate);
            log.Info("PlatterHeightUISystem registered.");

            updateSystem.UpdateAt<DistrictPolicySerializationSystem>(SystemUpdatePhase.Serialize);
            log.Info("DistrictPolicySerializationSystem registered.");

            // Height enforcement runs as its own system in the simulation group. It used to be a
            // Harmony postfix on ZoneSpawnSystem.OnUpdate, but doing structural changes from
            // inside another system's update broke Game.SafeCommandBufferSystem for the rest of
            // the frame ("Trying to create EntityCommandBuffer when it's not allowed!").
            updateSystem.UpdateAt<DistrictHeightPolicySystem>(SystemUpdatePhase.GameSimulation);
            log.Info("DistrictHeightPolicySystem registered.");
        }

        // Serves the zone-type icons shown under each height tier in the district panel.
        // Failure here is not fatal — the panel still works, the icons just render as
        // broken images — so it must never take the rest of OnLoad down with it.
        private void RegisterUIHost()
        {
            var iconsPath = Path.Combine(ModDirectory, "icons");
            if (!Directory.Exists(iconsPath))
            {
                log.Warn($"Icons directory not found at {iconsPath} — zone icons will not render.");
                return;
            }

            try
            {
                UIManager.defaultUISystem.AddHostLocation(kUIHostName, iconsPath);
                m_UIHostRegistered = true;
                log.Info($"UI host '{kUIHostName}' registered at {iconsPath}");
            }
            catch (System.Exception e)
            {
                log.Warn($"Could not register UI host '{kUIHostName}': {e.Message}");
            }
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

            if (m_UIHostRegistered)
            {
                try { UIManager.defaultUISystem.RemoveHostLocation(kUIHostName); }
                catch (System.Exception e) { log.Warn($"Could not remove UI host: {e.Message}"); }
                m_UIHostRegistered = false;
            }

            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}
