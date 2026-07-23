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

    function setBoxState(box, active) {
        box.style.border     = '2px solid ' + (active ? '#4a9eff' : '#888');
        box.style.background = active ? '#4a9eff' : 'transparent';
        box.textContent      = active ? '✓' : '';
    }

    function createPanel() {
        if (document.getElementById('height-policy-panel')) return document.getElementById('height-policy-panel');
        const panel = document.createElement('div');
        panel.id = 'height-policy-panel';
        panel.style.cssText = 'position:absolute;bottom:220px;right:20px;background:rgba(30,30,30,0.95);color:#e8e8e8;border-radius:6px;padding:12px 16px;min-width:220px;font-family:sans-serif;font-size:13px;box-shadow:0 2px 8px rgba(0,0,0,0.5);display:none;z-index:10000;pointer-events:auto;';

        const heading = document.createElement('div');
        heading.textContent = 'Height Policy';
        heading.style.cssText = 'font-weight:bold;font-size:14px;margin-bottom:10px;border-bottom:1px solid #555;padding-bottom:6px;';
        panel.appendChild(heading);

        const list = document.createElement('div');
        list.id = 'height-policy-list';
        panel.appendChild(list);

        // Build each tier row exactly once. Updates mutate these nodes in place.
        ALL_TIERS.forEach(function(tier) {
            const row = document.createElement('div');
            row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:5px 4px;cursor:pointer;border-radius:4px;pointer-events:auto;user-select:none;';

            const box = document.createElement('div');
            box.style.cssText = 'width:14px;height:14px;border-radius:3px;flex-shrink:0;display:flex;align-items:center;justify-content:center;font-size:10px;color:#fff;font-weight:bold;pointer-events:none;';

            const label = document.createElement('span');
            label.textContent = formatRange(tier);
            label.style.pointerEvents = 'none';

            setBoxState(box, false);
            row.appendChild(box);
            row.appendChild(label);

            function onClick(e) {
                e.preventDefault();
                e.stopPropagation();
            }
            // Optimistic flip on mousedown, then notify C#. Guards on all phases
            // in case click forwarding to the 3D world keys off a different event.
            row.addEventListener('mousedown', function(e) {
                e.preventDefault();
                e.stopPropagation();
                const nowActive = !activeTiersSet.has(tier);
                if (nowActive) activeTiersSet.add(tier); else activeTiersSet.delete(tier);
                setBoxState(box, nowActive);
                engine.trigger('districtHeightPolicy.toggleTier', tier);
            }, true);
            row.addEventListener('mouseup', onClick, true);
            row.addEventListener('click', onClick, true);

            rowEls[tier] = { row: row, box: box, label: label };
            list.appendChild(row);
        });

        document.body.appendChild(panel);
        return panel;
    }

    function applyActiveTiers() {
        ALL_TIERS.forEach(function(tier) {
            const entry = rowEls[tier];
            if (entry) setBoxState(entry.box, activeTiersSet.has(tier));
        });
    }

    function init() {
        panelEl = createPanel();

        // districtMod.update is pushed from DistrictPolicyUISystem.OnUpdate every frame via
        // view.TriggerEvent.
        engine.on('districtMod.update', function(isDistrict, activeTiers, tierRanges) {
            if (!panelEl) panelEl = createPanel();
            panelEl.style.display = isDistrict ? 'block' : 'none';

            const rangesKey = tierRanges || '';
            if (rangesKey !== lastRangesKey) {
                lastRangesKey = rangesKey;
                tierRangeMap = parseTierRanges(rangesKey);
                ALL_TIERS.forEach(function(tier) {
                    const entry = rowEls[tier];
                    if (entry) entry.label.textContent = formatRange(tier);
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
