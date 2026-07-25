using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using DistrictMod.Components;
using DistrictMod.Data;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Stamps a newly placed Platter parcel with the height restriction ticked in the Height
    // Restriction section of Platter's tool panel. Nothing else writes ParcelOverrideStore.
    //
    // New parcels are found by diffing the current parcel set against the ones already seen, not by
    // querying Game.Common.Created. Created is unusable here: CleanUpSystem strips it from
    // SystemUpdatePhase.Cleanup, which is pumped every *rendered* frame, and parcels are routinely
    // plopped while the simulation is paused — where a GameSimulation-phase system does not run at
    // all. A Created query would record almost nothing, and nothing whatsoever while paused. The
    // diff is immune to phase, update interval and pause alike.
    //
    // Runs at ModificationEnd, which ModificationSystem pumps every frame including while paused,
    // after the tool-apply passes have finished constructing the entity.
    public partial class PlatterParcelOverrideSystem : GameSystemBase
    {
        // Bulldozed parcels are cleaned up on a slow cadence — this is bookkeeping hygiene, not
        // correctness, and it walks two collections.
        private const int kPruneInterval = 64;
        private int m_Ticks;

        private EntityQuery m_ParcelQuery;
        private bool m_QueryReady;

        protected override void OnDestroy()
        {
            if (m_QueryReady) m_ParcelQuery.Dispose();
            base.OnDestroy();
        }

        // Built here rather than in OnCreate because Platter's component types do not resolve that
        // early, and with EntityManager.CreateEntityQuery rather than GetEntityQuery: the latter also
        // declares the access on this system, and doing that mid-OnUpdate mutates the dependency set
        // the scheduler has already computed for the frame.
        private bool EnsureParcelQuery(Type parcelType)
        {
            if (m_QueryReady) return true;

            try
            {
                m_ParcelQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly(parcelType) },
                    // Placement previews and parcels already on their way out are not placements.
                    None = new[]
                    {
                        ComponentType.ReadOnly<Temp>(),
                        ComponentType.ReadOnly<Deleted>(),
                    },
                });
                m_QueryReady = true;
                Mod.log.Info($"[PlatterParcelOverrideSystem] Watching {parcelType.FullName}.");
                return true;
            }
            catch (Exception e)
            {
                // Fault dedupes, so a type that is never queryable says so once, not every frame.
                PlatterInterop.Fault($"could not query {parcelType.FullName}: {e.Message}");
                return false;
            }
        }

        protected override void OnUpdate()
        {
            // Placements made while the integration is switched off record nothing. Existing
            // overrides are untouched — see the note in Setting.EnablePlatterIntegration.
            if (!LotPolicyState.PlatterIntegration) return;

            var parcelType = PlatterInterop.GetParcelComponentType(World);
            if (parcelType == null) return;              // Platter absent, or not loaded yet
            if (!EnsureParcelQuery(parcelType)) return;

            var parcels = m_ParcelQuery.ToEntityArray(Allocator.Temp);
            int seen = parcels.Length;          // read before the array is disposed below
            try
            {
                if (LotPolicyState.ParcelScanNeedsPrime)
                {
                    Prime(parcels);
                    return;
                }

                var pending = ParcelOverrideStore.SnapshotPending();

                for (int i = 0; i < parcels.Length; i++)
                {
                    // Add returns false for a parcel already known, which is every parcel on every
                    // tick after the one it appeared on.
                    if (!LotPolicyState.KnownParcels.Add(parcels[i])) continue;

                    // Seen for the first time, but the panel is not asking for a restriction. The
                    // parcel is left to its district, and is now known so it will not be
                    // reconsidered if the panel changes later.
                    if (pending == null) continue;

                    ParcelOverrideStore.SetTiers(parcels[i], new HashSet<HeightTier>(pending));
                    Mod.log.Info($"[PlatterParcelOverrideSystem] Parcel {parcels[i].Index} " +
                                 $"restricted to {string.Join(",", pending)}.");
                }
            }
            finally
            {
                parcels.Dispose();
            }

            Prune(seen);
        }

        // The first scan of a session learns the parcel set and records nothing, so that loading a
        // city does not look like every parcel in it was just placed.
        private void Prime(NativeArray<Entity> parcels)
        {
            LotPolicyState.KnownParcels.Clear();
            for (int i = 0; i < parcels.Length; i++)
                LotPolicyState.KnownParcels.Add(parcels[i]);

            LotPolicyState.ParcelScanNeedsPrime = false;

            // Worth an Info line: the two numbers should be consistent with each other across a
            // reload. Overrides greatly outnumbering parcels, or dropping to zero when parcels did
            // not, is the signature of parcel entities not surviving a load the way this assumes.
            Mod.log.Info($"[PlatterParcelOverrideSystem] Primed with {parcels.Length} existing " +
                         $"parcel(s); {ParcelOverrideStore.Count} override(s) restored from save.");
        }

        private void Prune(int seenThisTick)
        {
            if (++m_Ticks < kPruneInterval) return;
            m_Ticks = 0;

            // An empty query while overrides exist reads as "Platter has not populated its parcels
            // yet", not "every parcel was bulldozed". Pruning on that would delete the lot.
            if (seenThisTick == 0 && ParcelOverrideStore.Count > 0) return;

            ParcelOverrideStore.PruneDestroyed(EntityManager);
            LotPolicyState.PruneKnownParcels(EntityManager);
        }
    }
}
