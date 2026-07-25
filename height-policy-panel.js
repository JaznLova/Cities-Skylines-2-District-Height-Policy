(function () {
    'use strict';

    // Display names only (not the ranges) — ranges come live from C# via tierRanges,
    // since they're user-configurable in Options and must never be hardcoded here.
    const TIER_NAMES = {
        Small:      'Small',
        Medium:     'Medium',
        Large:      'Large',
        Tall:       'Tall',
        SuperTall:  'Super Tall',
        Skyscraper: 'Skyscraper',
    };
    const ALL_TIERS = ['Small','Medium','Large','Tall','SuperTall','Skyscraper'];
    let tierRangeMap = {};
    let lastRangesKey = null;

    // Height span of the buildings each zone type can produce. Unlike the tier ranges these
    // are facts about the game's assets, not user settings, so they live here rather than
    // being threaded through C#. Display only — enforcement never reads them.
    // Served from the coui host Mod.RegisterUIHost points at the mod's icons folder.
    const ICON_HOST = 'coui://districtheightpolicy/';
    const ZONES = [
        { label: 'Residential Low',        icon: 'ZoneResidentialLow.png',       min: 0,  max: 24  },
        { label: 'Residential Medium Row', icon: 'ZoneResidentialMediumRow.png', min: 0,  max: 24  },
        { label: 'Commercial High',        icon: 'ZoneNACommercialHigh.png',     min: 14, max: 62  },
        { label: 'Residential Medium',     icon: 'ZoneResidentialMedium.png',    min: 15, max: 36  },
        { label: 'Residential Low Rent',   icon: 'ZoneResidentialLowRent.png',   min: 15, max: 66  },
        { label: 'Residential Mixed',      icon: 'ZoneResidentialMixed.png',     min: 17, max: 52  },
        { label: 'Residential High',       icon: 'ZoneResidentialHigh.png',      min: 33, max: 195 },
        { label: 'Office High',            icon: 'ZoneOfficeHigh.png',           min: 50, max: 200 },
    ];

    // A zone belongs under a tier when the two height spans overlap. Computed rather than
    // hardcoded because the tier ranges are editable in Options — a fixed per-tier list
    // would go stale the moment someone drags a slider.
    function zonesForTier(tier) {
        const r = tierRangeMap[tier];
        if (!r) return [];
        const tMin = parseFloat(r.min), tMax = parseFloat(r.max);
        if (isNaN(tMin) || isNaN(tMax)) return [];
        return ZONES.filter(function(z) { return z.max > tMin && z.min < tMax; });
    }

    function formatRange(tier) {
        const r = tierRangeMap[tier];
        if (!r) return TIER_NAMES[tier] || tier;
        return (TIER_NAMES[tier] || tier) + ' (' + r.min + '-' + r.max + 'm)';
    }

    function parseTierRanges(str) {
        const map = {};
        (str || '').split(',').filter(Boolean).forEach(function(entry) {
            const parts = entry.split(':');
            if (parts.length !== 3) return;
            map[parts[0]] = { min: parts[1], max: parts[2] };
        });
        return map;
    }

    let panelEl = null;
    let activeTiersSet = new Set();
    // Row DOM nodes are built ONCE and cached here, keyed by tier name.
    const rowEls = {};    // tier -> { row, box }
    let lastActiveKey = null;   // last-applied activeTiers string, to skip redundant updates

    // The checkmark and the expander arrow are drawn with CSS borders rather than written as
    // text. cohtml's bundled font has no glyph for '✓' or '▸' and renders both as an empty
    // box, so nothing here may depend on a character being available.
    function setBoxState(entry, active) {
        entry.box.style.border     = '2px solid ' + (active ? '#4a9eff' : '#888');
        entry.box.style.background = active ? '#4a9eff' : 'transparent';
        entry.tick.style.display   = active ? 'block' : 'none';
    }

    // Right-pointing when collapsed, down-pointing when expanded.
    function setCaretState(arrow, open) {
        arrow.style.cssText = open
            ? 'width:0;height:0;pointer-events:none;border-left:5px solid transparent;border-right:5px solid transparent;border-top:7px solid #aaa;'
            : 'width:0;height:0;pointer-events:none;border-top:5px solid transparent;border-bottom:5px solid transparent;border-left:7px solid #aaa;';
    }

    function createPanel() {
        if (document.getElementById('height-policy-panel')) return document.getElementById('height-policy-panel');
        const panel = document.createElement('div');
        panel.id = 'height-policy-panel';
        panel.style.cssText = 'position:absolute;bottom:220px;right:20px;background:rgba(30,30,30,0.95);color:#e8e8e8;border-radius:6px;padding:12px 16px;width:250px;box-sizing:border-box;font-family:sans-serif;font-size:13px;box-shadow:0 2px 8px rgba(0,0,0,0.5);display:none;z-index:10000;pointer-events:auto;';

        const heading = document.createElement('div');
        heading.textContent = 'Height Policy';
        heading.style.cssText = 'font-weight:bold;font-size:14px;margin-bottom:10px;border-bottom:1px solid #555;padding-bottom:6px;';
        panel.appendChild(heading);

        const tip = document.createElement('div');
        tip.textContent = 'Tip: use multiple policies and zoning types for the best asset variety.';
        tip.style.cssText = 'font-size:11px;font-style:italic;color:#bbb;margin-bottom:10px;word-wrap:break-word;';
        panel.appendChild(tip);

        const list = document.createElement('div');
        list.id = 'height-policy-list';
        panel.appendChild(list);

        // Filled in from the live fallback setting each frame — see districtMod.update.
        const fallbackNote = document.createElement('div');
        fallbackNote.id = 'height-policy-fallback-note';
        fallbackNote.style.cssText = 'font-size:11px;color:#999;margin-top:10px;padding-top:8px;border-top:1px solid #555;word-wrap:break-word;';
        panel.appendChild(fallbackNote);

        // Build each tier row exactly once. Updates mutate these nodes in place.
        ALL_TIERS.forEach(function(tier) {
            const group = document.createElement('div');

            const row = document.createElement('div');
            row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:5px 4px;cursor:pointer;border-radius:4px;pointer-events:auto;user-select:none;';

            const box = document.createElement('div');
            box.style.cssText = 'width:18px;height:18px;border-radius:3px;flex-shrink:0;display:flex;align-items:center;justify-content:center;pointer-events:none;';

            // Checkmark: two borders of a box, rotated 45°. Shown only when the tier is active.
            const tick = document.createElement('div');
            tick.style.cssText = 'display:none;width:5px;height:9px;margin-bottom:2px;border-right:2px solid #fff;border-bottom:2px solid #fff;transform:rotate(45deg);pointer-events:none;';
            box.appendChild(tick);

            const label = document.createElement('span');
            label.textContent = formatRange(tier);
            label.style.pointerEvents = 'none';

            // Expander for the zone strip. Deliberately the only child of the row that keeps
            // its own pointer events, so the row handler below can recognise it by e.target.
            // The triangle inside stays pointer-events:none so e.target is always this span.
            const caret = document.createElement('span');
            caret.style.cssText = 'margin-left:auto;padding:4px 6px;flex-shrink:0;display:flex;align-items:center;pointer-events:auto;';
            const arrow = document.createElement('div');
            setCaretState(arrow, false);
            caret.appendChild(arrow);

            const strip = document.createElement('div');
            strip.style.cssText = 'display:none;flex-wrap:wrap;gap:5px;padding:2px 4px 8px 30px;pointer-events:none;';

            row.appendChild(box);
            row.appendChild(label);
            row.appendChild(caret);
            group.appendChild(row);
            group.appendChild(strip);

            function onClick(e) {
                e.preventDefault();
                e.stopPropagation();
            }
            // Optimistic flip on mousedown, then notify C#. Guards on all phases
            // in case click forwarding to the 3D world keys off a different event.
            row.addEventListener('mousedown', function(e) {
                e.preventDefault();
                e.stopPropagation();

                // This listener is on the row in the CAPTURE phase, so it runs before any
                // handler on the caret itself — the caret cannot opt out by stopping
                // propagation. Expanding must therefore be handled here, by target.
                if (e.target === caret) {
                    const open = strip.style.display === 'none';
                    strip.style.display = open ? 'flex' : 'none';
                    setCaretState(arrow, open);
                    return;
                }

                const nowActive = !activeTiersSet.has(tier);
                if (nowActive) activeTiersSet.add(tier); else activeTiersSet.delete(tier);
                setBoxState(rowEls[tier], nowActive);
                engine.trigger('districtHeightPolicy.toggleTier', tier);
            }, true);
            row.addEventListener('mouseup', onClick, true);
            row.addEventListener('click', onClick, true);

            rowEls[tier] = { row: row, box: box, tick: tick, label: label, caret: caret, arrow: arrow, strip: strip };
            setBoxState(rowEls[tier], false);
            list.appendChild(group);
        });

        document.body.appendChild(panel);
        return panel;
    }

    // Refills a tier's icon strip from the current tier ranges. Called whenever the ranges
    // change, so edits made in Options are reflected without a reload.
    function renderStrip(tier) {
        const entry = rowEls[tier];
        if (!entry) return;
        const strip = entry.strip;
        while (strip.firstChild) strip.removeChild(strip.firstChild);

        const zones = zonesForTier(tier);
        if (zones.length === 0) {
            const empty = document.createElement('span');
            empty.textContent = 'No matching zones';
            empty.style.cssText = 'color:#999;font-style:italic;font-size:11px;';
            strip.appendChild(empty);
            return;
        }

        zones.forEach(function(z) {
            const img = document.createElement('img');
            img.src = ICON_HOST + z.icon;
            img.title = z.label + ' (' + z.min + '-' + z.max + 'm)';
            img.style.cssText = 'width:28px;height:28px;flex-shrink:0;';
            strip.appendChild(img);
        });
    }

    function applyActiveTiers() {
        ALL_TIERS.forEach(function(tier) {
            const entry = rowEls[tier];
            if (entry) setBoxState(entry, activeTiersSet.has(tier));
        });
    }

    const FALLBACK_NOTES = {
        DezonePlot:  'Fallsback to removing zoning if no suitable building exists. Can change in settings.',
        KeepBuilding: 'Fallsback to keeping the last spawned building if no suitable one exists. Can change in settings.',
    };
    let lastFallback = null;

    function init() {
        panelEl = createPanel();
        const fallbackNote = document.getElementById('height-policy-fallback-note');

        // districtMod.update is pushed from DistrictPolicyUISystem.OnUpdate every frame via
        // view.TriggerEvent.
        engine.on('districtMod.update', function(isDistrict, activeTiers, tierRanges, fallback) {
            if (!panelEl) panelEl = createPanel();
            panelEl.style.display = isDistrict ? 'block' : 'none';

            if (fallback !== lastFallback) {
                lastFallback = fallback;
                if (fallbackNote) fallbackNote.textContent = FALLBACK_NOTES[fallback] || '';
            }

            const rangesKey = tierRanges || '';
            if (rangesKey !== lastRangesKey) {
                lastRangesKey = rangesKey;
                tierRangeMap = parseTierRanges(rangesKey);
                ALL_TIERS.forEach(function(tier) {
                    const entry = rowEls[tier];
                    if (entry) entry.label.textContent = formatRange(tier);
                    renderStrip(tier);
                });
            }

            if (!isDistrict) { lastActiveKey = null; return; }

            const key = activeTiers || '';
            // Only touch the DOM when the active set actually changed, not every frame.
            if (key === lastActiveKey) return;
            lastActiveKey = key;
            activeTiersSet = new Set(key ? key.split(',').filter(Boolean) : []);
            applyActiveTiers();
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
