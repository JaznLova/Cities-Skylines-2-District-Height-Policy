# District Height Policy

A Cities Skylines 2 mod that lets you set custom height limits for residential zones within each district, creating natural skyline transitions from suburbs to downtown towers.

## What It Does

Instead of having all residential buildings spawn at uniform heights across your city, District Height Policy lets you define height preferences per district through the Settings menu. When a building would spawn in a zoned lot, the mod checks whether it fits your district's height policy:

- If the building height is within the limit, it spawns normally.
- If it's too tall or short, it's rejected so another prefab can be tried instead.
- After 10 reroll attempts, if no building fits, the mod gracefully falls back to standard zoning for that lot.

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

**Note**: The mod is currently designed for Residential zones only.

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
