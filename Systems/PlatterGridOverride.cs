using System.Collections.Generic;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Makes the building counts Platter shows — the numbers in the cells of its Parcel Size grid,
    // and the same figures in its zone dropdown and tooltips — reflect the height tiers ticked in
    // our Height Restriction section, so an unbuildable parcel size reads as 0 before it is placed
    // rather than after nothing grows on it.
    //
    // Everything Platter displays as a count comes from Total in its P_BuildingCacheSystem map, so
    // this rewrites that map in place rather than trying to intercept a call or rewrite the DOM.
    // The alternatives were both worse: a Harmony patch would mean taking a hard dependency on an
    // 0Harmony.dll that no Cities II install actually provides (Platter ships its own copy), and
    // rewriting the grid's text from JavaScript would mean fighting React for it on every render.
    //
    // The rewrite is not destructive. Restore() puts back the unfiltered figures, and is called
    // whenever the restriction is cleared, the integration is switched off, or the tool is done
    // with — so Platter's own numbers come back the moment this stops having an opinion.
    internal class PlatterGridOverride
    {
        private readonly BuildingHeightScan m_Scan;

        // Whether our numbers are currently in Platter's map. Tracked rather than inferred so
        // Restore() is a no-op when nothing was ever written.
        private bool m_Applied;

        // The restriction the map currently reflects, so an unchanged selection does no work.
        private string m_AppliedKey;

        private bool m_WarnedMismatch;

        public PlatterGridOverride(BuildingHeightScan scan)
        {
            m_Scan = scan;
        }

        // Called every UI tick. Cheap when nothing changed: the key compare short-circuits before
        // any reflection happens.
        public void Sync(World world, ICollection<HeightTier> tiers, string rangesKey)
        {
            bool wanted = LotPolicyState.PlatterIntegration && tiers != null && tiers.Count > 0;

            if (!wanted)
            {
                Restore(world);
                return;
            }

            if (!m_Scan.EnsureBuilt() || !PlatterInterop.CanOverrideCounts(world)) return;

            // The ranges are part of the key because they decide which tier a height falls in, so
            // dragging a slider in Options has to reapply.
            string key = string.Join(",", tiers) + "/" + rangesKey;
            if (m_Applied && key == m_AppliedKey && StillApplied(world, tiers)) return;

            int written = 0;
            foreach (var k in m_Scan.Keys)
            {
                int restricted = m_Scan.CountFor(k.Zone, k.Width, k.Depth, tiers);
                if (!PlatterInterop.TryUpdateTotal(world, k.Zone, k.Width, k.Depth, restricted, out int previous))
                    continue;
                written++;
                VerifyBaseline(k, previous);
            }

            m_Applied = true;
            m_AppliedKey = key;
            bool refreshed = PlatterInterop.InvalidateGridBindings(world);

            Mod.log.Info($"[PlatterGridOverride] Applied {string.Join(",", tiers)} to {written} " +
                         $"zone/size combinations in Platter's grid (refresh={refreshed}).");
        }

        // Platter clears and repopulates its cache on every game load, which wipes our numbers
        // without telling us — and since the selection has not changed, Sync would otherwise
        // short-circuit forever and leave the grid unfiltered. So one entry is spot-checked
        // through Platter's own getter each tick; if it no longer reads back what we wrote, the
        // whole set is reapplied. One reflected call per tick is cheaper than tracking load events
        // across a mod boundary we do not control.
        private bool StillApplied(World world, ICollection<HeightTier> tiers)
        {
            if (m_Scan.Keys.Count == 0) return true;

            var probe = m_Scan.Keys[0];
            int actual = PlatterInterop.GetBuildingCount(world, probe.Zone, probe.Width, probe.Depth);
            if (actual < 0) return true;    // unreadable — nothing useful to conclude

            return actual == m_Scan.CountFor(probe.Zone, probe.Width, probe.Depth, tiers);
        }

        public void Restore(World world)
        {
            if (!m_Applied) return;

            // Deliberately recomputed rather than restored from a snapshot taken at apply time. A
            // snapshot would be wrong after Platter rebuilds its cache on a save load, whereas the
            // unfiltered count is a property of the installed prefabs and so is always current.
            // VerifyBaseline is what proves the two agree.
            int written = 0;
            foreach (var k in m_Scan.Keys)
            {
                int unrestricted = m_Scan.CountFor(k.Zone, k.Width, k.Depth, null);
                if (PlatterInterop.TryUpdateTotal(world, k.Zone, k.Width, k.Depth, unrestricted, out _))
                    written++;
            }

            m_Applied = false;
            m_AppliedKey = null;
            if (written > 0)
            {
                PlatterInterop.InvalidateGridBindings(world);
                Mod.log.Info($"[PlatterGridOverride] Restored Platter's own counts for {written} " +
                             "zone/size combinations.");
            }
        }

        // The value found in the map before the first write should equal our own unfiltered count,
        // because the scan replicates Platter's bucketing rules exactly. If it does not, Platter
        // has changed how it counts and Restore() would be writing wrong numbers back — worth one
        // warning, since the symptom otherwise is Platter's grid quietly disagreeing with itself.
        private void VerifyBaseline(BuildingHeightScan.ZoneLot k, int previous)
        {
            if (m_WarnedMismatch || m_Applied) return;

            int unrestricted = m_Scan.CountFor(k.Zone, k.Width, k.Depth, null);
            if (previous == unrestricted || previous < 0) return;

            m_WarnedMismatch = true;
            Mod.log.Warn($"[PlatterGridOverride] Platter reports {previous} buildings for zone " +
                         $"{k.Zone} at {k.Width}x{k.Depth} but this mod counts {unrestricted}. " +
                         "Platter's counting rules have changed; grid numbers may be wrong. " +
                         "Turn off Platter integration in this mod's settings if they look off.");
        }
    }
}
