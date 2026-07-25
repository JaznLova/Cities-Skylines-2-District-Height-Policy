using System.IO;
using System.Linq;
using Colossal.UI.Binding;
using Game.UI;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictMod.Systems;
using DistrictHeightPolicy;

namespace DistrictMod.UI
{
    // Adds a "Height Restriction" section to Platter's tool options panel: a dropdown holding a
    // checkbox per height tier, with the same names and (live) ranges the district panel shows.
    // Selection is recorded in LotPolicyState.PlatterPendingTiers; nothing enforces it yet.
    //
    // The section is injected into the cohtml view with ExecuteScript and built from raw DOM,
    // the same route DistrictPolicyUISystem uses. Platter's panel is React-owned, so the script
    // re-checks every tick that our node is still there rather than inserting once.
    public partial class PlatterHeightUISystem : UISystemBase
    {
        private const string kGroup = "districtHeightPolicyPlatter";
        private const string kScript = "platter-height-section.js";

        private cohtml.Net.View _view;
        private bool _injected;
        private int _viewAttempts;
        private const int kMaxViewAttempts = 300;

        private BuildingHeightScan _scan;
        private PlatterGridOverride _gridOverride;

        // Per-tier building counts for the current selection, used by the panel only to grey a
        // tier nothing can fill. These change only when the pre-zone, the parcel size or a tier
        // range changes, none of which happen per tick — so the payload is rebuilt on change and
        // otherwise resent from here. Rescanning the prefab list six times a second is not free.
        private string _countsKey = null;
        private string _counts = "";

        // The script's anchor search walks the whole DOM, so it runs on a slow cadence instead
        // of every frame. Platter's panel appearing a quarter second late is not noticeable.
        private const int kTickInterval = 15;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();

            AddBinding(new TriggerBinding<string>(kGroup, "toggleTier", OnToggleTier));
            AddBinding(new TriggerBinding(kGroup, "jsReady", OnJsReady));
            AddBinding(new TriggerBinding<string>(kGroup, "jsLog", OnJsLog));

            _scan = new BuildingHeightScan(World);
            _gridOverride = new PlatterGridOverride(_scan);

            Mod.log.Info("[PlatterHeightUISystem] Bindings registered.");
        }

        protected override void OnUpdate()
        {
            TryInjectScript();

            if (_view == null) return;
            if (++_frame < kTickInterval) return;
            _frame = 0;

            string ranges = DistrictPolicyUISystem.SerializeTierRanges();

            // The counts themselves are not sent to the panel: they are written straight into
            // Platter's own count cache, so they show up in the cells of its Parcel Size grid.
            // See PlatterGridOverride.
            _gridOverride.Sync(World, LotPolicyState.PlatterPendingTiers, ranges);

            bool hasSelection = false;
            if (LotPolicyState.PlatterIntegration
                && PlatterInterop.TryGetSelection(World, out var zoneIndex, out var parcelSize))
            {
                hasSelection = true;
                UpdateCounts(ranges, zoneIndex, parcelSize);
            }

            // The enabled flag is pushed rather than gating the tick, so the script gets told to
            // take its section back down when the setting is switched off mid-session.
            _view.TriggerEvent("districtModPlatter.tick",
                LotPolicyState.PlatterIntegration,
                ranges,
                SerializeSelectedTiers(),
                hasSelection ? _counts : "");
        }

        // Rebuilds the per-tier counts only when something they depend on actually changed. The
        // tier ranges are part of the key because they decide which tier a building falls into, so
        // dragging a slider in Options has to invalidate this.
        private void UpdateCounts(string ranges, ushort zoneIndex, Unity.Mathematics.int2 parcelSize)
        {
            string key = zoneIndex + "/" + parcelSize.x + "x" + parcelSize.y + "/" + ranges;
            if (key == _countsKey) return;
            _countsKey = key;

            _counts = _scan.Serialize(zoneIndex, parcelSize.x, parcelSize.y, DistrictPolicyUISystem.kAllTiers);
        }

        // Same "Tier,Tier" shape the district panel's activeTiers binding uses.
        private static string SerializeSelectedTiers()
        {
            return string.Join(",", DistrictPolicyUISystem.kAllTiers
                .Where(LotPolicyState.PlatterPendingTiers.Contains)
                .Select(t => t.ToString()));
        }

        private void TryInjectScript()
        {
            if (_injected || _viewAttempts >= kMaxViewAttempts) return;

            _view = Game.SceneFlow.GameManager.instance?.userInterface?.view?.View;
            if (_view == null)
            {
                if (++_viewAttempts == kMaxViewAttempts)
                    Mod.log.Warn("[PlatterHeightUISystem] View never available.");
                return;
            }

            var scriptPath = Path.Combine(Mod.ModDirectory, kScript);
            if (!File.Exists(scriptPath))
            {
                Mod.log.Warn("[PlatterHeightUISystem] JS not found: " + scriptPath);
                _viewAttempts = kMaxViewAttempts;
                return;
            }

            _view.ExecuteScript(File.ReadAllText(scriptPath));
            _injected = true;
            Mod.log.Info("[PlatterHeightUISystem] Script injected.");
        }

        private void OnToggleTier(string tierName)
        {
            if (!LotPolicyState.PlatterIntegration) return;
            if (!System.Enum.TryParse<HeightTier>(tierName, out var tier)) return;

            if (!LotPolicyState.PlatterPendingTiers.Remove(tier))
                LotPolicyState.PlatterPendingTiers.Add(tier);

            Mod.log.Info($"[PlatterHeightUISystem] Pending tiers: {SerializeSelectedTiers()}");
        }

        private void OnJsReady()
        {
            Mod.log.Info("[PlatterHeightUISystem] JS reported ready.");
        }

        // The injected script has no console this mod's log file can see, so its state changes
        // (anchor found / lost) come back over this trigger. It only fires on change.
        private void OnJsLog(string message)
        {
            Mod.log.Info("[PlatterHeightUISystem][js] " + message);
        }
    }
}
