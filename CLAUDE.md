# Platter Integration — Plan and Findings

Status: **not implemented.** A full implementation was attempted in one pass, built cleanly, and
did not work in game. It has been reverted to the published version. This file records the plan,
the facts established along the way, and the order to rebuild it in so each step can be proven
in game before the next one is written.

Read the "Verified facts" section before writing any code — it is the part that cost the most to
establish and the part the failed attempt got wrong.

## Goal

Let a single hand-plopped [Platter](https://github.com/lucarager/CS2-Platter) parcel carry its own
height tier, overriding the height policy of the district it sits in. Platter's tool panel gains a
"Height Restriction" section listing each tier with the number of level-1 building assets matching
the selected zone + parcel size + that tier, so an impossible combination is visible before placing.

## Verified facts (Platter 1.6.3.0, read from the shipped dll)

These were confirmed by decompiling the installed assembly, not by reading Platter's GitHub `main`.
The two disagree, and the first attempt failed because it trusted GitHub.

Installed at:
`%LocalAppData%Low\Colossal Order\Cities Skylines II\.cache\Mods\pdx_mods\125278_48\Platter.dll`

**Namespaces are not what the source suggests.** There is no `Platter.Prefabs` namespace. Platter
declares its prefab types into the *game's* namespace:
- `Game.Prefabs.ParcelPlaceholderPrefab` — public `int m_LotWidth`, `int m_LotDepth`, `ZoneBlockPrefab m_ZoneBlock`
- `Game.Prefabs.ParcelSelectorPrefab`
- `Game.Prefabs.ParcelPrefab`

**`Platter.Systems.P_UISystem` exposes exactly what the integration needs, publicly.** Prefer these
over reaching into `ObjectToolSystem`'s held prefab, which is what the failed attempt did:
- `ZoneType PreZoneType` — the selected "Pre-Zone"
- `int2 SelectedParcelSize` — selected width x depth
- `bool CurrentlyUsingParcelsInObjectTool` — whether Platter's parcel tool is active
- `bool CurrentlyUsingZoneTool`, `bool ShowZones`, `bool ShowContourLines`

**Prefab naming** (`Platter.Utils.ParcelUtils`): real parcels are `"Parcel {w}x{d}"`, placement
placeholders are `"ParcelPlaceholder {w}x{d}"`. Matching the prefix `"Parcel "` *with the trailing
space* selects real parcels and excludes placeholders.

**Components** (`Platter.Components`) — relevant ones:
- `LinkedParcel { Entity m_Parcel }` — on growable buildings. Present on growables generally, so
  presence proves nothing; only a non-null `m_Parcel` means "this building is on a parcel".
- `Parcel { Entity m_RoadEdge, Entity m_Building, float m_CurvePosition, ZoneType m_PreZoneType, ParcelState m_State }`
- `ParcelData { int2 m_LotSize, Entity m_ZoneBlockPrefab }`
- `ParcelSubBlock { Entity m_SubBlock }` (buffer) — parcels own their zone blocks, separate from
  the road edge's `SubBlock` buffer that `DezoneLot` walks.

**Platter expects size mismatch.** It ships the string *"No {X}x{Y} buildings in selected zone.
Smaller buildings may spawn on parcel."* A 4x4 building on a 6x6 parcel is normal.

### How to re-derive these facts

ReflectionOnly loading is useless here — anything deriving from a game type fails to resolve and
silently disappears from `GetTypes()`, which produces confident wrong answers. Use Cecil:

```powershell
Add-Type -Path "E:\SteamLibrary\steamapps\common\Cities Skylines II\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($platterDll)
$asm.MainModule.Types | Where-Object { $_.Name -match 'UISystem' } | ForEach-Object {
  $_.Properties | ForEach-Object { "{0} {1}" -f $_.PropertyType.Name, $_.Name } }
```

String literals in IL (for prefab naming, locale keys) come from walking
`$type.Methods.Body.Instructions` for `Ldstr`.

## What actually happened on the failed attempt

1. First run: `[PlatterInterop] Platter 1.6.3.0 detected.` then
   `[WARN] type Platter.Prefabs.ParcelPlaceholderPrefab not found`. Correct behaviour — the
   log-once diagnostic did its job and named the fault. The panel section correctly refused to
   render because the interop reported itself unusable.
2. Fixed the interop onto `P_UISystem`'s real properties. Rebuilt clean.
3. Second run: still nothing in Platter's UI.

**The remaining suspect is the UI bundle, not the C# side.** Step 2 below exists to settle that
question on its own before any more C# is written. Specifically unverified:
- whether the game loads `DistrictHeightPolicy.js` from the mod folder at all
- whether `mod.json` needs anything beyond `{id, author, version, dependencies}`
- whether `game-ui/game/components/tool-options/tool-options-panel.tsx` / export `ToolOptionsPanel`
  is the correct extension point in the current game build
- whether Platter's panel is even rendered through that vanilla export, or through its own
  separate UI surface

Note the C# side was never proven either — no log line ever confirmed the bindings were being
read, because nothing was rendering to read them.

## Decisions already made (do not re-litigate)

- **Ship against whatever Platter version is current; fix breakage reactively.** No version
  gating, no compatibility UI. Keep one log-once diagnostic per distinct reflection failure,
  naming the member and the loaded Platter version — that is what diagnosed the first failure.
- **Overrides persist in the save** (they are position-keyed, so no entity remapping concerns).
- **Editing a district's policy does not clear parcel overrides** — an override is a deliberate
  per-parcel choice.
- **Dezone Plot must never dezone a Platter parcel.** Confirmed in game: with a Small policy and
  no matching assets, a 6x6 parcel was fragmented into 2x4 + 4x4 because `DezoneLot` strips the
  *building's* footprint, not the parcel's, and the game re-blocks the remainder. Detect via
  `LinkedParcel.m_Parcel != Entity.Null` and keep the building instead. **This is independently
  useful and does not depend on any UI work** — see step 1.

## Incremental build order

Each step ends with something provable in game. Do not start a step before the previous one is
confirmed in `Logs/DistrictHeightPolicy.log` or on screen.

**Step 1 — Parcel dezone guard (no UI, no settings).**
Add reflection-only `IsOnParcel(EntityManager, Entity)` reading `Platter.Components.LinkedParcel`.
In `DistrictHeightPolicySystem.ApplyFallback`, before the DezonePlot branch, keep the building if
it is on a parcel. *Prove:* place a 6x6 parcel in a district with an unsatisfiable policy; the
parcel stays whole. This is the one piece with confirmed in-game value; land it first and alone.

**Step 2 — Prove a UI mod renders at all.**
No integration logic. Revive `UI/` and register something unmissable (a fixed-position coloured
box via `moduleRegistry.append("Game", ...)`). Note `UI/mod.json` ships `id: "DistrictMod"`, which
deploys the bundle to `Mods/DistrictMod` — a *different folder from the dll* — so it would never
load; it must be `DistrictHeightPolicy`. *Prove:* the box appears in game. If it does not, the
whole UI approach is the problem and nothing downstream matters.

**Step 3 — Prove the extension point.**
Swap the box to `moduleRegistry.extend` on the tool-options panel and render a static label.
*Prove:* the label appears when Platter's tool is open. If the vanilla export is wrong, find the
right one before writing any bindings.

**Step 4 — Prove the C# → UI binding.**
Add `DistrictPolicyPlatterUISystem` with a single `isPlatterActive` bool binding driven by
`P_UISystem.CurrentlyUsingParcelsInObjectTool`. *Prove:* the label appears only while Platter's
parcel tool is active.

**Step 5 — Counts.**
`PlatterBuildingCountSystem`: bucket level-1 `SpawnableBuildingData` prefabs by
`(zone index, lot width, lot depth, tier)` using `ObjectGeometryData.m_Bounds.max.y` and
`BuildingHeightLoader.TierRanges`. Match `IsBuildingAllowed`'s bounds exactly (exclusive min,
inclusive max). Rebuild when tier ranges change. *Prove:* counts change with zone and parcel size.

**Step 6 — Selection and enforcement.**
Tier dropdown writing a pending tier; `PlatterOverrideSystem` records
`PositionOverrides[PositionKey(transform.m_Position)] = tier` for `Created` entities whose prefab
name starts with `"Parcel "` (exclude `Temp`). Enforcement reads the override in place of the
district's tiers. *Prove:* a parcel placed with a tier keeps that tier in a district whose policy
disagrees.

**Step 7 — Persistence.**
Versioned save format + `EnablePlatterIntegration` setting. *Prove:* override survives reload, and
a pre-change save still loads.

## Implementation notes worth keeping

- **`PositionKey` must be shared.** It is currently a private method on `DistrictHeightPolicySystem`
  taking an `Entity`. An override is written from the parcel's transform and read from the
  building's transform; any drift between two copies makes overrides permanently unreachable with
  no error. Extract to `LotPolicyState.PositionKey(float3)` and delegate.
- **`PositionOverrides` must be excluded from `ClearSessionState()` and `ResetLotState()`.**
  Everything else there is entity-keyed and must be dropped on load; overrides are position-keyed
  and restored from the save. Only a brand-new city clears them.
- **Overridden lots must not be grandfathered** when their district first gains a policy.
- **Save format has no version header.** Old format is `count` then `(Entity, mask)` pairs. To add
  a block without breaking existing saves, write a negative sentinel first (a real count is never
  negative) followed by a version int; a reader seeing a non-negative first int treats it as the
  old layout.
- **An override should bypass the low-density eligibility gate.** That gate stops a district-wide
  policy dezoning every low-density shop; an explicit per-parcel choice is the opposite case.
- Reserve a single-element `HeightTier[]` on the system for the override's `IsBuildingAllowed`
  call rather than allocating per building.

## Build

`npm install` once in `UI/`, then `dotnet build -c Release`. If wiring the UI build into the
csproj, skip it (with a warning) when `UI/node_modules` is absent rather than failing the build —
the C# mod is fully usable without the bundle.
