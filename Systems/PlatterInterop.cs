using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using Unity.Mathematics;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Reflection-only access to the Platter mod. Platter is a soft dependency: this assembly must
    // load, and every other feature must keep working, when Platter is absent, and it must not
    // take the mod down when a Platter update moves something.
    //
    // Two entry points are used, both public on Platter's side:
    //   * Platter.Systems.P_UISystem — the tool panel's own state: which parcel size and pre-zone
    //     are selected, and whether the parcel tool is even active. Preferred over inspecting
    //     ObjectToolSystem's held prefab, which is what an earlier attempt at this did and which
    //     broke as soon as Platter changed how it holds its placeholder.
    //   * Platter.Systems.P_BuildingCacheSystem.GetBuildingCount — Platter's own count of level-1
    //     growables for a zone and lot size, used here as the unfiltered total the per-tier counts
    //     are a subset of. Taking it from Platter rather than recomputing it means the number
    //     shown next to "of" always agrees with Platter's own tooltip.
    //
    // Every distinct failure is named exactly once in the log along with the loaded Platter
    // version. That diagnostic is what identified the wrong namespace assumption on the first
    // attempt at this integration, so it is worth keeping.
    internal static class PlatterInterop
    {
        private const string kUISystemType = "Platter.Systems.P_UISystem";
        private const string kCacheSystemType = "Platter.Systems.P_BuildingCacheSystem";

        private static bool s_Resolved;
        private static int s_Attempts;
        private const int kAttemptsBeforeGivingUpQuietly = 200;

        private static Type s_UIType, s_CacheType;
        private static PropertyInfo s_PreZoneType, s_SelectedParcelSize, s_UsingParcelTool;
        private static FieldInfo s_ZoneTypeIndex;
        private static MethodInfo s_GetBuildingCount;

        private static FieldInfo s_LastCountsZone, s_LastCountsSize;
        private static FieldInfo s_CountField, s_TotalField;
        private static MethodInfo s_MapContainsKey, s_MapGetItem, s_MapSetItem;

        private static readonly HashSet<string> s_LoggedFaults = new();

        public static string Version { get; private set; }

        private static void Fault(string what)
        {
            if (!s_LoggedFaults.Add(what)) return;
            Mod.log.Warn($"[PlatterInterop] {what} (Platter {Version ?? "not loaded"})");
        }

        // Platter's types are found through the systems the world has actually instantiated, not
        // by scanning AppDomain assemblies. The assembly scan is what broke the first working
        // version of this: Platter ships as "Platter" and the name matched, but enumerating every
        // loaded assembly and calling GetName() on each is not safe in this process, and the
        // failure was silent because the result had already been latched.
        //
        // Consequently a miss here is treated as "not loaded yet" and retried, since our UI tick
        // can run before Platter's systems are created. After enough tries it goes quiet and
        // says so once — that is the "Platter is simply not installed" case.
        private static bool Probe(World world)
        {
            if (s_Resolved) return s_UIType != null;
            if (world == null) return false;

            foreach (var system in world.Systems)
            {
                var type = system?.GetType();
                if (type == null) continue;
                if (type.FullName == kUISystemType) s_UIType = type;
                else if (type.FullName == kCacheSystemType) s_CacheType = type;
            }

            if (s_UIType == null)
            {
                if (++s_Attempts == kAttemptsBeforeGivingUpQuietly)
                {
                    s_Resolved = true;
                    Mod.log.Info("[PlatterInterop] Platter systems not present — integration idle.");
                }
                return false;
            }

            s_Resolved = true;
            Version = s_UIType.Assembly.GetName().Version?.ToString();
            Mod.log.Info($"[PlatterInterop] Platter {Version} detected.");

            // NonPublic matters: CurrentlyUsingParcelsInObjectTool is a public property with a
            // private getter, so a default GetProperty lookup does not see it at all.
            const BindingFlags kAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            s_PreZoneType = s_UIType.GetProperty("PreZoneType", kAny);
            s_SelectedParcelSize = s_UIType.GetProperty("SelectedParcelSize", kAny);
            s_UsingParcelTool = s_UIType.GetProperty("CurrentlyUsingParcelsInObjectTool", kAny);

            // How the grid is made to notice a rewritten cache — see InvalidateGridBindings.
            s_LastCountsZone = s_UIType.GetField("m_LastBuildingCountsZoneIndex", kAny);
            s_LastCountsSize = s_UIType.GetField("m_LastBuildingCountsBySize", kAny);
            if (s_LastCountsZone == null) Fault("P_UISystem.m_LastBuildingCountsZoneIndex not found");
            if (s_LastCountsSize == null) Fault("P_UISystem.m_LastBuildingCountsBySize not found");

            if (s_PreZoneType == null) Fault("P_UISystem.PreZoneType not found");
            if (s_SelectedParcelSize == null) Fault("P_UISystem.SelectedParcelSize not found");
            if (s_UsingParcelTool == null) Fault("P_UISystem.CurrentlyUsingParcelsInObjectTool not found");

            // ZoneType is a game type, so this resolves against Game.dll and not Platter — but it
            // is only ever needed to unpack what PreZoneType returns.
            if (s_PreZoneType != null)
            {
                s_ZoneTypeIndex = s_PreZoneType.PropertyType.GetField("m_Index");
                if (s_ZoneTypeIndex == null) Fault("ZoneType.m_Index not found");
            }

            if (s_CacheType == null) Fault($"type {kCacheSystemType} not found");
            else
            {
                s_GetBuildingCount = s_CacheType.GetMethod("GetBuildingCount",
                    new[] { typeof(ushort), typeof(int), typeof(int) });
                if (s_GetBuildingCount == null) Fault("P_BuildingCacheSystem.GetBuildingCount not found");
                ProbeCountCache();
            }

            return true;
        }

        // Members of the count cache itself. Everything Platter shows as a building count — the
        // parcel-size grid, the zone dropdown, the tooltips — reads Total out of this one map via
        // GetBuildingCount, so rewriting entries in it is how the height restriction reaches all
        // of them at once, in Platter's own rendering, with no patching and nothing to fight over
        // the DOM. See PlatterGridOverride for the write side.
        private static void ProbeCountCache()
        {
            s_CountField = s_CacheType.GetField("m_BuildingCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (s_CountField == null) { Fault("P_BuildingCacheSystem.m_BuildingCount not found"); return; }

            var mapType = s_CountField.FieldType;
            if (!mapType.IsGenericType || mapType.GetGenericArguments().Length != 2)
            {
                Fault($"m_BuildingCount is {mapType.Name}, not a two-argument map");
                s_CountField = null;
                return;
            }

            s_MapContainsKey = mapType.GetMethod("ContainsKey");
            s_MapGetItem = mapType.GetMethod("get_Item");
            s_MapSetItem = mapType.GetMethod("set_Item");
            s_TotalField = mapType.GetGenericArguments()[1].GetField("Total");

            if (s_MapContainsKey == null) Fault("NativeHashMap.ContainsKey not found");
            if (s_MapGetItem == null) Fault("NativeHashMap.get_Item not found");
            if (s_MapSetItem == null) Fault("NativeHashMap.set_Item not found");
            if (s_TotalField == null) Fault("BuildingAccessCount.Total not found");
        }

        private static ComponentSystemBase GetSystem(World world, Type type)
        {
            if (world == null || type == null) return null;
            try { return world.GetExistingSystemManaged(type); }
            catch (Exception e) { Fault($"{type.Name} not resolvable: {e.Message}"); return null; }
        }

        // The panel state Platter's parcel tool is currently in. Returns false whenever the tool
        // is not active or anything at all could not be read — callers treat that as "no parcel
        // selection to describe", which is also the correct answer when Platter is not installed.
        public static bool TryGetSelection(World world, out ushort zoneIndex, out int2 parcelSize)
        {
            zoneIndex = 0;
            parcelSize = default;

            if (!Probe(world) || s_UsingParcelTool == null || s_PreZoneType == null
                || s_SelectedParcelSize == null || s_ZoneTypeIndex == null) return false;

            var system = GetSystem(world, s_UIType);
            if (system == null) return false;

            try
            {
                if (!(bool)s_UsingParcelTool.GetValue(system)) return false;
                zoneIndex = (ushort)s_ZoneTypeIndex.GetValue(s_PreZoneType.GetValue(system));
                parcelSize = (int2)s_SelectedParcelSize.GetValue(system);
                return true;
            }
            catch (Exception e)
            {
                Fault($"reading P_UISystem state failed: {e.Message}");
                return false;
            }
        }

        // Platter's unfiltered count for a zone and lot size. -1 means "unavailable", which the
        // panel renders as no total rather than as a zero.
        public static int GetBuildingCount(World world, ushort zoneIndex, int lotWidth, int lotDepth)
        {
            if (!Probe(world) || s_GetBuildingCount == null) return -1;

            var system = GetSystem(world, s_CacheType);
            if (system == null) return -1;

            try
            {
                return (int)s_GetBuildingCount.Invoke(system,
                    new object[] { zoneIndex, lotWidth, lotDepth });
            }
            catch (Exception e)
            {
                Fault($"GetBuildingCount failed: {e.Message}");
                return -1;
            }
        }

        public static bool CanOverrideCounts(World world)
        {
            return Probe(world) && s_CountField != null && s_TotalField != null
                && s_MapContainsKey != null && s_MapGetItem != null && s_MapSetItem != null;
        }

        // Reads and rewrites the Total for one (zone, lot size) entry of Platter's count cache.
        //
        // The map is a NativeHashMap, a struct wrapping a pointer into unmanaged memory. Fetching
        // it through FieldInfo boxes a copy of that struct, but the copy carries the same pointer,
        // so writing through it lands in the buffer Platter itself reads — which is the whole
        // reason this works without patching any code.
        //
        // Only entries that already exist are touched, never added: Platter's HasBuildings tests
        // key presence rather than Total, so a restricted count of zero still leaves parcel
        // placement enabled. Restricting a height must not stop you plopping the parcel.
        public static bool TryUpdateTotal(World world, ushort zoneIndex, int lotWidth, int lotDepth,
                                          int newTotal, out int previousTotal)
        {
            previousTotal = -1;
            if (!CanOverrideCounts(world)) return false;

            var system = GetSystem(world, s_CacheType);
            if (system == null) return false;

            try
            {
                object map = s_CountField.GetValue(system);
                object key = new float3(zoneIndex, lotWidth, lotDepth);

                if (!(bool)s_MapContainsKey.Invoke(map, new[] { key })) return false;

                object value = s_MapGetItem.Invoke(map, new[] { key });
                previousTotal = (int)s_TotalField.GetValue(value);
                if (previousTotal == newTotal) return true;

                s_TotalField.SetValue(value, newTotal);
                s_MapSetItem.Invoke(map, new[] { key, value });
                return true;
            }
            catch (Exception e)
            {
                Fault($"rewriting m_BuildingCount failed: {e.Message}");
                return false;
            }
        }

        // Rewriting the cache is not enough on its own: P_UISystem.UpdateBindings only recomputes
        // the grid when the selected pre-zone index or parcel size differs from the last one it
        // pushed, so a cache changed underneath it goes unnoticed and the panel keeps showing the
        // numbers it captured earlier.
        //
        // Setting those two guard fields back to -1 is exactly what Platter does to itself when
        // its zone cache version changes, and it makes Platter re-read the cache and re-push both
        // count bindings on its own next update. Nothing here writes to a binding directly — the
        // numbers still arrive in the panel through Platter's own code path.
        public static bool InvalidateGridBindings(World world)
        {
            if (!Probe(world) || s_LastCountsZone == null || s_LastCountsSize == null) return false;

            var system = GetSystem(world, s_UIType);
            if (system == null) return false;

            try
            {
                s_LastCountsZone.SetValue(system, -1);
                s_LastCountsSize.SetValue(system, new int2(-1, -1));
                return true;
            }
            catch (Exception e)
            {
                Fault($"invalidating count bindings failed: {e.Message}");
                return false;
            }
        }
    }
}
