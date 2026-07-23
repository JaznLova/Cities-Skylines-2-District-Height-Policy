using System.IO;
using System.Linq;
using Colossal.UI.Binding;
using Game.UI;
using Game.UI.InGame;
using Game.Areas;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.UI
{
    public partial class DistrictPolicyUISystem : UISystemBase
    {
        private const string kGroup = "districtHeightPolicy";

        private SelectedInfoUISystem _selectedInfoSystem;
        private ValueBinding<bool>   _isDistrictBinding;
        private ValueBinding<string> _activeTiersBinding;

        private cohtml.Net.View _view;
        private bool _injected;
        private int _viewAttempts;
        private const int kMaxViewAttempts = 300;

        private static readonly HeightTier[] kAllTiers =
            { HeightTier.Small, HeightTier.Medium, HeightTier.Large, HeightTier.Tall, HeightTier.SuperTall, HeightTier.Skyscraper };

        protected override void OnCreate()
        {
            base.OnCreate();
            _selectedInfoSystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();

            _isDistrictBinding  = new ValueBinding<bool>  (kGroup, "isDistrict",  false);
            _activeTiersBinding = new ValueBinding<string>(kGroup, "activeTiers",  "");

            AddBinding(_isDistrictBinding);
            AddBinding(_activeTiersBinding);
            AddBinding(new TriggerBinding<string>(kGroup, "toggleTier", OnToggleTier));

            Mod.log.Info("[DistrictPolicyUISystem] Bindings registered.");
        }

        // Ranges only change via the settings menu, not every frame, but re-serializing a
        // handful of floats per frame is cheap and keeps the panel trivially always-correct.
        private static string SerializeTierRanges()
        {
            var parts = new System.Collections.Generic.List<string>(kAllTiers.Length);
            foreach (var tier in kAllTiers)
            {
                if (BuildingHeightLoader.TierRanges.TryGetValue(tier, out var r))
                    parts.Add($"{tier}:{r.Min}:{r.Max}");
            }
            return string.Join(",", parts);
        }

        protected override void OnUpdate()
        {
            var selected = _selectedInfoSystem.selectedEntity;
            bool isDistrict = selected != Entity.Null
                && EntityManager.HasComponent<District>(selected);

            string activeTiers = isDistrict
                ? (DistrictPolicyStore.GetTiers(selected) is var t && t != null && t.Count > 0
                    ? string.Join(",", t.Select(x => x.ToString()))
                    : "")
                : "";

            _isDistrictBinding.Update(isDistrict);
            _activeTiersBinding.Update(activeTiers);

            TryInjectPanel();

            // ValueBinding subscriptions don't reach scripts injected via ExecuteScript,
            // so the panel is fed by pushing directly to its engine.on listener. tierRanges
            // reflects whatever the user currently has set in Options, so the panel's labels
            // are never stale/hardcoded.
            _view?.TriggerEvent("districtMod.update", isDistrict, activeTiers, SerializeTierRanges());
        }

        private void TryInjectPanel()
        {
            if (_injected || _viewAttempts >= kMaxViewAttempts) return;

            _view = Game.SceneFlow.GameManager.instance?.userInterface?.view?.View;
            if (_view == null)
            {
                if (++_viewAttempts == kMaxViewAttempts)
                    Mod.log.Warn("[DistrictPolicyUISystem] View never available.");
                return;
            }

            var scriptPath = Path.Combine(Mod.ModDirectory, "height-policy-panel.js");
            if (!File.Exists(scriptPath))
            {
                Mod.log.Warn("[DistrictPolicyUISystem] JS not found: " + scriptPath);
                _viewAttempts = kMaxViewAttempts;
                return;
            }

            _view.ExecuteScript(File.ReadAllText(scriptPath));
            _injected = true;
            Mod.log.Info("[DistrictPolicyUISystem] Script injected.");
        }

        private void OnToggleTier(string tierName)
        {
            var selected = _selectedInfoSystem.selectedEntity;
            if (selected == Entity.Null || !EntityManager.HasComponent<District>(selected)) return;
            if (!System.Enum.TryParse<HeightTier>(tierName, out var tier)) return;
            DistrictPolicyStore.ToggleTier(selected, tier);
            DistrictMod.Harmony.ZoneSpawnPatch.ResetLotState();
            Mod.log.Info($"[DistrictPolicyUISystem] Toggled {tierName} for district {selected.Index}");
        }
    }
}
