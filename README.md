# District Height Policy

A Cities Skylines 2 mod that lets you set custom height limits within each district, creating natural skyline transitions from suburbs to downtown towers.

## What's New in 2.0

- **Fallback System Control** — Choose between _Dezone Plot_ (keep your skyline clean) or _Keep Building_ (accept whatever spawns)
- **High Commercial & Office Support** — These zones now respect district height policies
- **Improved UI Guidance** — Zone type icons and helpful tips in the Settings panel

## What It Does

Instead of having buildings spawn at uniform heights across your city, District Height Policy lets you define height preferences per district through the Settings menu. When a building would spawn in a zoned lot, the mod checks whether it fits your district's height policy:

- If the building height is within the limit, it spawns normally.
- If it's too tall or short, it's rejected so another prefab can be tried instead.
- After 10 reroll attempts (configurable), if no building fits, the mod applies your chosen **Fallback System** for that lot:
  - **Dezone Plot** (default) — the zoning is removed from the lot, so it stays empty rather than holding a building that breaks your policy.
  - **Keep Building** — the mod gives up and keeps whatever building last spawned.

This gives you fine-grained control without requiring extra dependencies—everything is configurable through the in-game Settings menu.

## How to Use

1. Enable the mod in your load order.
2. Start or load a city.
3. Open Settings and navigate to District Height Policy.
4. Select each district and assign a height policy (Small, Medium, Large, or combinations).
5. Zoned lots in that district will now spawn according to your settings.

## Installation

Download from Paradox Mods and subscribe. The mod is self-contained; no other mod dependencies are required.

## Tips for Best Results

**Maintain Asset Diversity**: Strict height policies can limit the variety of buildings that spawn. Consider using at least two height preferences per district or mixing zone types for more natural neighborhoods.

**Optimize Lot Sizes**: Manually deleting unwanted buildings can trigger improved plot size generation, which sometimes helps match your height policies more reliably.

## Which Zones Are Affected

**Controlled by Policy:**
- **Residential** — all densities
- **Commercial** — high density only
- **Office** — high density only

**Not Affected:**
- **Low Density Commercial & Office** — deliberately left alone. Those assets are all under about 10m, so any policy above the Small tier would reject every building the game offers for the lot, leaving it dezoned. Easier to skip them entirely.

The mod works across every region pack (North America, Europe, China, France, UK, Japan, and more) — it identifies zone types by name, not region.

## Building from Source

Requirements:
- .NET 4.8
- Cities Skylines 2 modding tools (CSII_TOOLPATH environment variable set)

Build with:
```
dotnet build -c Release
```

Compiled mod is deployed to `%LocalAppData%\Colossal Order\Cities Skylines II\Mods\DistrictHeightPolicy`.

## Credits

This mod uses Lib.Harmony 2.2.2 (MIT-licensed), which is bundled with the mod. Harmony is included to support mod patching and requires no separate installation.

## License

MIT License. See the Lib.Harmony license for details on bundled dependencies.
