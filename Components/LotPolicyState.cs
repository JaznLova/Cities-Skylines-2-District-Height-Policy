using System.Collections.Generic;
using Unity.Entities;

namespace DistrictMod.Components
{
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

        // Per-lot reroll tracking (keyed by quantized world position, stable across respawns).
        internal static readonly Dictionary<long, int> RerollCounts = new();
        internal static readonly HashSet<long> UnsatisfiableLots = new();

        // Rerolls allowed before a lot is given up on and its spawned building kept as-is.
        // Exposed via Setting.MaxRerolls (1-25); defaults to the original hardcoded value.
        public static int MaxRerolls { get; set; } = 10;

        // Called when a district policy changes so lots are re-evaluated rather than
        // staying frozen as "unsatisfiable".
        public static void ResetLotState()
        {
            RerollCounts.Clear();
            UnsatisfiableLots.Clear();
        }

        // Called whenever a save is loaded (or a new city starts): all of this state is
        // keyed by per-session entity data, so it must never carry over across a reload —
        // entity ids are remapped on load and a stale "already approved"/"already
        // grandfathered" decision could otherwise apply to a different district or building.
        public static void ClearSessionState()
        {
            ApprovedEntities.Clear();
            ActivatedDistricts.Clear();
            ResetLotState();
        }
    }
}
