(function () {
    'use strict';

    // Adds a "Height Restriction" section to Platter's tool options panel: a dropdown holding a
    // checkbox per height tier. Driven by PlatterHeightUISystem, which pushes the enabled flag,
    // the live tier ranges and the current selection on every tick.
    //
    // Injected with view.ExecuteScript and built from raw DOM, like height-policy-panel.js —
    // not through a webpack bundle + moduleRegistry. Platter renders its options through the
    // vanilla mouse-tool-options module, so every class name in the live DOM is a build-hashed
    // name like "section_ABC". Nothing here may depend on those hashes: the anchor section is
    // found by its visible title text, and our section is a clone of it so it inherits whatever
    // the current build's styling happens to be.

    const SECTION_ID = 'dhp-platter-section';
    const OUR_TITLE = 'Height Restriction';

    const TIER_NAMES = {
        Small:      'Small',
        Medium:     'Medium',
        Large:      'Large',
        Tall:       'Tall',
        SuperTall:  'Super Tall',
        Skyscraper: 'Skyscraper',
    };
    const ALL_TIERS = ['Small', 'Medium', 'Large', 'Tall', 'SuperTall', 'Skyscraper'];

    // Titles Platter ships for its parcel tool sections, in preference order — we insert after
    // the first one found. From the mod's L10n/lang/en-US.json (PlatterMod.UI.SectionTitle.*).
    const ANCHOR_TITLES = ['Parcel Size', 'Pre-Zone', 'Snapping', 'Tool Mode'];

    // Ranges are user-editable in Options, so labels are never hardcoded — they come from C#.
    let tierRangeMap = {};
    let lastRangesKey = null;
    let selectedSet = new Set();
    let lastSelectedKey = null;
    let expanded = false;

    // tier -> {count} for Platter's selected zone and parcel size. The numbers themselves are not
    // rendered here — they are written into Platter's own count cache so they appear in the cells
    // of its Parcel Size grid instead. All this is used for is greying a tier nothing can fill.
    //
    // A zone icon per row was tried and removed: an icon next to "Medium" reads as "this zone is
    // allowed", when what it actually meant was "this zone could supply a building of this height
    // at this parcel size" — a much narrower claim that no icon can carry on its own.
    let countMap = {};
    let lastCountsKey = null;

    // Rebuilt whenever React drops our node, so these are re-pointed rather than long-lived.
    let els = null;         // { wrapper, summary, list, note, arrow, rows: {tier: {...}} }
    let lastLoggedState = null;
    let describeAttempts = 0;

    function log(msg) {
        // Surfaced in the C# log via this trigger, since the injected script has no console the
        // mod's log file can see. Only fires on change.
        if (msg === lastLoggedState) return;
        lastLoggedState = msg;
        try { engine.trigger('districtHeightPolicyPlatter.jsLog', msg); } catch (e) { }
    }

    function parseTierRanges(str) {
        const map = {};
        (str || '').split(',').filter(Boolean).forEach(function (entry) {
            const parts = entry.split(':');
            if (parts.length !== 3) return;
            map[parts[0]] = { min: parts[1], max: parts[2] };
        });
        return map;
    }

    // "Tier~count;Tier~count;..."
    function parseCounts(str) {
        const map = {};
        (str || '').split(';').filter(Boolean).forEach(function (entry) {
            const parts = entry.split('~');
            if (parts.length < 2) return;
            map[parts[0]] = { count: parseInt(parts[1], 10) || 0 };
        });
        return map;
    }

    function tierLabel(tier) {
        const name = TIER_NAMES[tier] || tier;
        const r = tierRangeMap[tier];
        return r ? name + ' (' + r.min + '-' + r.max + 'm)' : name;
    }

    function summaryText() {
        const chosen = ALL_TIERS.filter(function (t) { return selectedSet.has(t); });
        if (chosen.length === 0) return 'No restriction';
        if (chosen.length === 1) return TIER_NAMES[chosen[0]] || chosen[0];
        // Every tier ticked permits every height, so it restricts nothing and no override is
        // recorded for it. Saying "All tiers" would read like a restriction being applied.
        if (chosen.length === ALL_TIERS.length) return 'No restriction (all tiers)';
        return chosen.length + ' tiers';
    }

    // The checkmark and the caret are drawn with CSS borders rather than written as text:
    // cohtml's bundled font has no glyph for '✓' or '▾' and renders both as an empty box.
    function setBoxState(row, active) {
        row.box.style.border = '2rem solid ' + (active ? '#4a9eff' : '#aaa');
        row.box.style.background = active ? '#4a9eff' : 'transparent';
        row.tick.style.display = active ? 'block' : 'none';
    }

    function setCaretState(arrow, open) {
        arrow.style.cssText = open
            ? 'width:0;height:0;pointer-events:none;border-left:5rem solid transparent;border-right:5rem solid transparent;border-top:7rem solid #ddd;'
            : 'width:0;height:0;pointer-events:none;border-top:5rem solid transparent;border-bottom:5rem solid transparent;border-left:7rem solid #ddd;';
    }

    function setExpanded(open) {
        expanded = open;
        if (!els) return;
        els.list.style.display = open ? 'block' : 'none';
        setCaretState(els.arrow, open);
    }

    // Finds the label element carrying one of Platter's section titles, then treats its parent
    // as the section row. Matches on visible text only — no assumption about class names or tag
    // beyond the element being the leaf that actually holds the text.
    function findAnchorSection() {
        // Single pass over the DOM, collecting whichever titles are present, rather than one
        // pass per title — this runs on every tick the panel is open.
        const found = {};
        const all = document.querySelectorAll('*');
        for (let i = 0; i < all.length; i++) {
            const el = all[i];
            if (el.children.length !== 0) continue;              // want the leaf, not an ancestor
            const text = (el.textContent || '').trim();
            if (ANCHOR_TITLES.indexOf(text) === -1 || found[text]) continue;
            const section = el.parentElement;
            if (section && !isOurs(section)) found[text] = { section: section, title: el };
        }
        for (let t = 0; t < ANCHOR_TITLES.length; t++) {
            const hit = found[ANCHOR_TITLES[t]];
            if (hit) return hit;
        }
        return null;
    }

    function isOurs(node) {
        while (node) {
            if (node.id === SECTION_ID) return true;
            node = node.parentElement;
        }
        return false;
    }

    // Diagnostic for when the anchor cannot be found: reports what the DOM actually looks like
    // around the wanted text so the structure can be read out of the log instead of guessed at.
    function describeDom() {
        const all = document.querySelectorAll('*');
        const parts = ['elements=' + all.length];

        for (let t = 0; t < ANCHOR_TITLES.length; t++) {
            const wanted = ANCHOR_TITLES[t];
            let deepest = null;
            for (let i = 0; i < all.length; i++) {
                const el = all[i];
                if ((el.textContent || '').indexOf(wanted) === -1) continue;
                deepest = el;   // document order: later matches are deeper or later siblings
            }
            if (!deepest) { parts.push('"' + wanted + '" NOT IN DOM'); continue; }

            const chain = [];
            let node = deepest;
            for (let d = 0; d < 5 && node; d++) {
                chain.push('<' + node.tagName.toLowerCase() + ' class="' + (node.className || '') +
                    '" children=' + node.children.length + '>');
                node = node.parentElement;
            }
            parts.push('"' + wanted + '" leafText=' + JSON.stringify((deepest.textContent || '').trim().slice(0, 40)) +
                ' chain=' + chain.join(' < '));
        }
        return parts.join(' | ');
    }

    // The game forwards clicks that reach the 3D world, so every interactive node swallows all
    // three mouse phases — the district panel needed the same treatment.
    function claimClicks(el, onPress) {
        el.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            onPress();
        }, true);
        el.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); }, true);
        el.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); }, true);
    }

    function buildSection(anchor) {
        // Outer wrapper is ours, not a clone: Platter's section is a single row (label left,
        // control right), which cannot hold a list that grows downwards. The clone inside keeps
        // the header looking native; the expanded list below it is full width.
        const wrapper = document.createElement('div');
        wrapper.id = SECTION_ID;

        const header = anchor.section.cloneNode(false);
        // Platter's own section styling is inherited via the clone, but the row layout is forced
        // here rather than trusted: the caret has to sit hard against the control's right edge,
        // and whether the cloned class happens to be a flex row is a build detail.
        header.style.display = 'flex';
        header.style.alignItems = 'center';
        header.style.width = '100%';
        header.style.boxSizing = 'border-box';

        const title = anchor.title.cloneNode(false);
        title.textContent = OUR_TITLE;
        title.style.flex = '0 0 auto';
        header.appendChild(title);

        // Dropdown control, styled after Platter's own Pre-Zone dropdown. space-between puts the
        // summary at the left inner edge and the caret at the right one, so the arrow lines up
        // with the panel edge instead of floating next to the text.
        const control = document.createElement('div');
        control.style.cssText = 'display:flex;align-items:center;justify-content:space-between;' +
            'margin-left:auto;flex:0 1 auto;min-width:150rem;height:36rem;' +
            'padding:0 8rem 0 10rem;box-sizing:border-box;background:rgba(0,0,0,0.4);' +
            'border:1rem solid rgba(255,255,255,0.25);border-radius:4rem;color:#fff;' +
            'font-size:14rem;cursor:pointer;pointer-events:auto;';

        const summary = document.createElement('span');
        summary.style.cssText = 'pointer-events:none;white-space:nowrap;overflow:hidden;' +
            'margin-right:8rem;';
        control.appendChild(summary);

        const caret = document.createElement('span');
        caret.style.cssText = 'flex:0 0 auto;display:flex;align-items:center;pointer-events:none;';
        const arrow = document.createElement('div');
        caret.appendChild(arrow);
        control.appendChild(caret);

        claimClicks(control, function () { setExpanded(!expanded); });
        header.appendChild(control);

        const list = document.createElement('div');
        list.style.cssText = 'display:none;width:100%;box-sizing:border-box;' +
            'padding:4rem 10rem 8rem 10rem;';

        const rows = {};
        ALL_TIERS.forEach(function (tier) {
            const row = document.createElement('div');
            // width:100% matters: without it the row is only as wide as its content, and then the
            // margin-left:auto that is supposed to pin the icons to the right edge resolves to 0
            // and they sit jammed against the label instead.
            //
            // Spacing between children is done with margins, never with flex `gap` — this cohtml
            // build silently ignores gap, which is what made the checkbox touch its label and the
            // zone icons run into each other.
            row.style.cssText = 'display:flex;align-items:center;width:100%;box-sizing:border-box;' +
                'padding:6rem 4rem;min-height:32rem;color:#fff;font-size:14rem;' +
                'cursor:pointer;pointer-events:auto;';

            const box = document.createElement('div');
            box.style.cssText = 'width:18rem;height:18rem;border-radius:3rem;flex-shrink:0;' +
                'margin-right:10rem;display:flex;align-items:center;justify-content:center;' +
                'pointer-events:none;';

            const tick = document.createElement('div');
            tick.style.cssText = 'display:none;width:5rem;height:9rem;margin-bottom:2rem;' +
                'border-right:2rem solid #fff;border-bottom:2rem solid #fff;' +
                'transform:rotate(45deg);pointer-events:none;';
            box.appendChild(tick);

            const label = document.createElement('span');
            label.textContent = tierLabel(tier);
            label.style.cssText = 'pointer-events:none;flex:1 1 auto;min-width:0;' +
                'white-space:nowrap;';

            row.appendChild(box);
            row.appendChild(label);

            // Flip optimistically, then tell C#. The next tick pushes the authoritative set
            // back and corrects this if the toggle was rejected.
            claimClicks(row, function () {
                const nowActive = !selectedSet.has(tier);
                if (nowActive) selectedSet.add(tier); else selectedSet.delete(tier);
                setBoxState(rows[tier], nowActive);
                summary.textContent = summaryText();
                engine.trigger('districtHeightPolicyPlatter.toggleTier', tier);
            });

            rows[tier] = { box: box, tick: tick, label: label };
            list.appendChild(row);
        });

        wrapper.appendChild(header);
        wrapper.appendChild(list);

        els = { wrapper: wrapper, summary: summary, list: list, arrow: arrow, rows: rows };
        summary.textContent = summaryText();
        ALL_TIERS.forEach(function (tier) { setBoxState(rows[tier], selectedSet.has(tier)); });
        renderCounts();
        setExpanded(expanded);
        return wrapper;
    }

    // A tier no building can fill at the selected zone and parcel size is greyed — the same fact
    // as the 0 Platter's own grid now shows for that combination. The tier stays tickable: it is a
    // statement about the current selection, not about the restriction being invalid.
    function renderCounts() {
        if (!els) return;

        ALL_TIERS.forEach(function (tier) {
            const info = countMap[tier];
            els.rows[tier].label.style.opacity = (!info || info.count > 0) ? '1' : '0.5';
        });
    }

    function remove() {
        const existing = document.getElementById(SECTION_ID);
        if (existing && existing.parentElement) existing.parentElement.removeChild(existing);
        els = null;
    }

    function applyRanges(rangesKey) {
        if (rangesKey === lastRangesKey) return;
        lastRangesKey = rangesKey;
        tierRangeMap = parseTierRanges(rangesKey);
        if (!els) return;
        ALL_TIERS.forEach(function (tier) { els.rows[tier].label.textContent = tierLabel(tier); });
    }

    function applySelection(selectedKey) {
        if (selectedKey === lastSelectedKey) return;
        lastSelectedKey = selectedKey;
        selectedSet = new Set(selectedKey ? selectedKey.split(',').filter(Boolean) : []);
        if (!els) return;
        ALL_TIERS.forEach(function (tier) { setBoxState(els.rows[tier], selectedSet.has(tier)); });
        els.summary.textContent = summaryText();
    }

    function applyCounts(countsKey) {
        if (countsKey === lastCountsKey) return;
        lastCountsKey = countsKey;
        countMap = parseCounts(countsKey);
        renderCounts();
    }

    function sync(enabled, tierRanges, selectedTiers, counts) {
        // Ranges, selection and icons are tracked even while the section is off screen, so a
        // rebuild comes up already correct instead of flashing stale values for a tick.
        applyRanges(tierRanges || '');
        applySelection(selectedTiers || '');
        applyCounts(counts || '');

        if (!enabled) {
            // Setting switched off: take our section back down and stop scanning the DOM.
            if (document.getElementById(SECTION_ID)) {
                remove();
                log('disabled in settings — section removed');
            } else {
                log('disabled in settings');
            }
            return;
        }

        const anchor = findAnchorSection();
        if (!anchor) {
            // Platter's panel is gone (tool closed), or a React re-render dropped our node with
            // it. Either way there is nothing to attach to; the next sync re-inserts.
            // Not an error, and the common case — Platter's tool is usually closed — so the
            // (expensive) DOM dump runs only for the first few misses, which is all that is
            // needed to diagnose a section that never appears at all.
            remove();
            log(describeAttempts++ < 3 ? 'anchor absent: ' + describeDom() : 'anchor absent');
            return;
        }

        // React owns this subtree and discards our node on re-render, so presence is re-checked
        // continuously rather than inserted once.
        const existing = document.getElementById(SECTION_ID);
        if (existing && existing.parentElement === anchor.section.parentElement) return;
        remove();

        const section = buildSection(anchor);
        anchor.section.parentElement.insertBefore(section, anchor.section.nextSibling);
        log('inserted after "' + (anchor.title.textContent || '').trim() + '"');
    }

    function init() {
        // Driven by the tick from PlatterHeightUISystem.OnUpdate rather than a timer, so it
        // stops costing anything when the system is not updating.
        engine.on('districtModPlatter.tick', sync);
        try { engine.trigger('districtHeightPolicyPlatter.jsReady'); } catch (e) { }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
