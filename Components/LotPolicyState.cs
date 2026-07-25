using System.Collections.Generic;
using Unity.Entities;

namespace DistrictMod.Components
{
    // What the mod does with a lot once MaxRerolls rerolls have failed to produce a building
    // the district's policy accepts.
    public enum FallbackMode
    {
        // Strip the zoning off the lot's cells so nothing respawns there.
        DezonePlot,
        // Original behavior: give up and keep whatever building last spawned.
        KeepBuilding,
    }


    // Per-session bookkeeping for the reroll loop in DistrictHeightPolicySystem. Static rather
    // than system state because Setting.PushToRuntime() and the serialization system both need
    // to poke it from outside the ECS world.
    public static class LotPolicyState
    {
        // Buildings that have been judged and accepted (either they satisfy the policy, they
        // predate it, or their lot was given up on). Keyed by the full Entity — Index alone is
        // reused across save/load and would let a stale decision apply to a different building.
        internal static readonly HashSet<Entity> ApprovedEntities = new();
        internal static readonly HashSet<Entity> ActivatedDistricts = new();

        // Zone prefab entity -> "is this a high density zone". Resolving it means a managed
        // PrefabSystem name lookup, which is far too slow to redo for every building every
        // update. Cleared with the rest of the session state because zone prefab entity ids
        // are remapped on save load.
        internal static readonly Dictionary<Entity, bool> HighDensityZoneCache = new();

        // Per-lot reroll tracking (keyed by quantized world position, stable across respawns).
        internal static readonly Dictionary<long, int> RerollCounts = new();
        internal static readonly HashSet<long> UnsatisfiableLots = new();

        // The same bookkeeping for lots governed by a parcel override, keyed by the parcel entity.
        // The position-keyed pair above cannot be reused for these: candidate buildings smaller than
        // the parcel sit at different centres, so every reroll would hash to a different key, the
        // counter would never pass 1, MaxRerolls would never be reached and the fallback would never
        // run — leaving an unsatisfiable parcel in a permanent spawn/delete loop instead.
        internal static readonly Dictionary<Entity, int> ParcelRerollCounts = new();
        internal static readonly HashSet<Entity> UnsatisfiableParcels = new();

        // Parcels this session has already seen. How a newly placed parcel is detected: the Created
        // tag is stripped by the game's CleanUpSystem every rendered frame, far too fast for a
        // simulation-phase system to catch, and it is never even set while the game is paused —
        // which is exactly when parcels get plopped. Diffing against this set is immune to both.
        internal static readonly HashSet<Entity> KnownParcels = new();

        // Set on load and for a new city. The first scan after that only learns the existing
        // parcels; without it, loading a save would look like every parcel in the city was just
        // placed and stamp them all with whatever happens to be ticked in the panel.
        internal static bool ParcelScanNeedsPrime = true;

        // Rerolls allowed before a lot is given up on and its spawned building kept as-is.
        // Exposed via Setting.MaxRerolls (1-25); defaults to the original hardcoded value.
        public static int MaxRerolls { get; set; } = 10;

        // What happens to a lot that has burned through its rerolls.
        // Exposed via Setting.Fallback; defaults to dezoning the plot.
        public static FallbackMode Fallback { get; set; } = FallbackMode.DezonePlot;

        // Platter soft dependency. Exposed via Setting.EnablePlatterIntegration so a Platter
        // update that breaks the integration can be switched off without uninstalling this mod,
        // leaving the district-based enforcement untouched.
        public static bool PlatterIntegration { get; set; } = true;

        // Tiers ticked in the Height Restriction section of Platter's tool panel — the policy a
        // parcel will be stamped with when it is placed. UI selection state only; enforcement
        // does not read it yet.
        public static readonly HashSet<Data.HeightTier> PlatterPendingTiers = new();

        // Called when a district policy changes so lots are re-evaluated rather than
        // staying frozen as "unsatisfiable".
        public static void ResetLotState()
        {
            RerollCounts.Clear();
            UnsatisfiableLots.Clear();

            // Parcels are re-evaluated too. A tier range edit changes which tier a height falls in,
            // so an override that was unsatisfiable a moment ago may be satisfiable now.
            ParcelRerollCounts.Clear();
            UnsatisfiableParcels.Clear();
        }

        // Forgets parcels that no longer exist, so the seen-set does not grow without bound in a
        // city where parcels are placed and bulldozed repeatedly.
        public static void PruneKnownParcels(EntityManager em)
        {
            if (KnownParcels.Count == 0) return;
            KnownParcels.RemoveWhere(p => !em.Exists(p));
        }

        // Called whenever a save is loaded (or a new city starts): all of this state is
        // keyed by per-session entity data, so it must never carry over across a reload —
        // entity ids are remapped on load and a stale "already approved"/"already
        // grandfathered" decision could otherwise apply to a different district or building.
        public static void ClearSessionState()
        {
            ApprovedEntities.Clear();
            ActivatedDistricts.Clear();
            HighDensityZoneCache.Clear();
            ResetLotState();

            // Note this does NOT clear ParcelOverrideStore, which is why that lives in its own type:
            // Deserialize fills the overrides and then calls this, so clearing them here would wipe
            // every override on every load. Only the seen-set is session state.
            KnownParcels.Clear();
            ParcelScanNeedsPrime = true;
        }
    }
}
