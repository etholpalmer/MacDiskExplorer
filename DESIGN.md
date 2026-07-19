---
name: MacDiskExplorer
description: A macOS storage briefing that leads with what is wrong.
colors:
  bg: "#f6f7f9"
  panel: "#ffffff"
  ink: "#1c2128"
  muted: "#6b7280"
  border: "#e5e7eb"
  accent: "#2563eb"
  accent-soft: "#eff6ff"
  status-ok: "#16a34a"
  status-warn: "#d97706"
  status-crit: "#dc2626"
  bg-dark: "#0d1117"
  panel-dark: "#161b22"
  ink-dark: "#e6edf3"
  muted-dark: "#8b949e"
  border-dark: "#30363d"
  accent-dark: "#58a6ff"
  accent-soft-dark: "#132033"
  status-ok-dark: "#3fb950"
  status-warn-dark: "#d29922"
  status-crit-dark: "#f85149"
typography:
  metric:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "1.9rem"
    fontWeight: 700
    lineHeight: 1
  headline:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "1.05rem"
    fontWeight: 650
    lineHeight: 1.45
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.45
  data:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "0.85rem"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "0.78rem"
    fontWeight: 400
    letterSpacing: "0.03em"
  micro:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif"
    fontSize: "0.72rem"
    fontWeight: 600
rounded:
  sm: "6px"
  md: "8px"
  lg: "12px"
  pill: "999px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
components:
  card:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "16px"
  panel-section:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
  panel-section-summary:
    textColor: "{colors.ink}"
    typography: "{typography.headline}"
    padding: "14px 18px"
  button-theme:
    backgroundColor: "{colors.bg}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "8px 13px"
  input-search:
    backgroundColor: "{colors.bg}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "8px 12px"
    width: "220px"
  sort-button:
    textColor: "{colors.ink}"
    typography: "{typography.data}"
    padding: "10px 13px"
    width: "100%"
  sort-button-hover:
    textColor: "{colors.accent}"
  badge-internal:
    backgroundColor: "{colors.accent-soft}"
    textColor: "{colors.accent}"
    rounded: "{rounded.pill}"
    padding: "2px 8px"
    typography: "{typography.micro}"
  capacity-bar:
    backgroundColor: "{colors.bg}"
    rounded: "{rounded.sm}"
    height: "18px"
    width: "120px"
---

# Design System: MacDiskExplorer

## 1. Overview

**Creative North Star: "The Briefing"**

This is a document prepared for someone who has to act. A sysadmin opens it on a Mac they did not configure, and the system's entire job is to put the finding in front of them before the inventory. Everything is ordered by consequence: what is failing, then what is at risk, then what merely exists. The reader who leaves after five seconds still leaves knowing the verdict.

Underneath that editorial ordering, the material is instrument-grade. Tables are the hero. Density is not a compromise here, it is the correct answer for a reader comparing twenty-seven volumes across twelve disks, and the system maximizes information per pixel inside each panel. The two ideas are not in tension: the briefing decides the *order and weight* of the sections, the instrument decides the *texture inside* them. Chrome recedes; controls are quiet until touched; the data is the only thing with real presence.

What this system rejects is its own worst-case failure mode: **enterprise monitoring clutter, in the Grafana and Nagios mold, where density arrives without hierarchy, every panel carries the same visual weight, nothing is prioritized, and the operator is left doing the ranking the tool should have done.** Volume of information is never the problem. Undifferentiated information is. Any change that makes two things of unequal consequence look equally loud is a regression.

**Key Characteristics:**
- Verdict first: warnings outrank inventory in both position and weight
- Instrument density inside panels, editorial hierarchy between them
- Flat at rest; depth is a response to state, never a decoration
- Status is never carried by hue alone
- Dual-theme native, with the dark ramp as a first-class citizen rather than an inversion

## 2. Colors

A restrained utility palette: two neutral surfaces, one accent reserved for interaction, and a three-stop status scale that is the only saturated color allowed to carry meaning.

### Primary
- **Signal Blue** (`#2563eb` light / `#58a6ff` dark): The single interaction accent. Reserved for focus rings, the active sort column, hover affordance on controls, and the internal-device badge. It marks *where you can act*, never decorates. Its dark-mode counterpart is lightened rather than merely desaturated so it clears 4.5:1 on the near-black panel.
- **Signal Blue Wash** (`#eff6ff` light / `#132033` dark): The tint behind row hover and the internal badge. Never used for large fills.

### Secondary
The status scale. These three are the only colors permitted to encode meaning, and they encode exactly one thing: health.
- **Verdict Green** (`#16a34a` light / `#3fb950` dark): Healthy. Capacity under 90%.
- **Verdict Amber** (`#d97706` light / `#d29922` dark): Warning. Capacity 90–94%, degraded state.
- **Verdict Red** (`#dc2626` light / `#f85149` dark): Critical. Capacity 95%+, SMART failure, removed device.

### Neutral
- **Cool Field** (`#f6f7f9` light / `#0d1117` dark): The page ground. Panels sit on it; it is never a content surface itself.
- **Panel White** (`#ffffff` light / `#161b22` dark): Every readable surface. Cards, disclosure panels, table bodies.
- **Ink** (`#1c2128` light / `#e6edf3` dark): Primary text. Carries all data values.
- **Muted Ink** (`#6b7280` light / `#8b949e` dark): Labels, counts, secondary metadata, the "-" absence marker. Never used for a data value the reader must read.
- **Hairline** (`#e5e7eb` light / `#30363d` dark): All 1px borders and table rules. This is the primary structural device in the system.

### Named Rules

**The Verdict Rule.** Green, amber, and red are reserved for health and severity. They may never be used for branding, category coding, chart series, or emphasis. If a new feature needs a color to distinguish non-health categories, it does not get one; it gets a label.

**The Never-Hue-Alone Rule.** Every status color is accompanied by a redundant non-color signal: a glyph, a word, or a number. A reader with deuteranopia, or a monochrome print of this page, must lose nothing. This is a hard accessibility requirement from PRODUCT.md, not a preference.

**The Provisional Palette Rule.** The current accent and status ramp are close to GitHub's stock defaults. They are recorded here as the honest current state, not as a defended choice, and are the system's known weakest point. Revisit with `/impeccable colorize` before treating this palette as settled.

## 3. Typography

**Display Font:** none. The system is deliberately single-family.
**Body Font:** system UI stack (`-apple-system`, `BlinkMacSystemFont`, `Segoe UI`, `Roboto`, sans-serif)
**Label/Mono Font:** none currently. Device identifiers and byte counts render in the same sans as prose.

**Character:** One family, many weights. A product surface like this does not need a display pairing; it needs a well-tuned sans that can hold a 1.9rem metric and a 0.72rem bar label without either looking accidental. The native stack also makes the report feel like it belongs to the machine it describes.

### Hierarchy
- **Metric** (700, 1.9rem, line-height 1): Summary card counts. The largest type in the system.
- **Headline** (650, 1.05rem, line-height 1.45): The host name in the sticky header, and disclosure summary titles.
- **Body** (400, 1rem, line-height 1.45): Prose and warning messages. Prose caps at 65–75ch; tabular content is exempt and may run to 120ch+.
- **Data** (400, 0.85rem): All table cells and headers. The workhorse size.
- **Label** (400, 0.78rem, letter-spacing 0.03em, uppercase): Summary card captions and the footer.
- **Micro** (600, 0.72rem): Capacity bar percentages and badges.

### Named Rules

**The Fixed-Scale Rule.** Sizes are a fixed rem scale, never `clamp()`. This is a product surface read at consistent DPI; fluid type that shrinks a metric inside a narrow column makes the report worse, not more responsive. Responsiveness here is structural (columns collapse, tables scroll), never typographic.

**The Consequence-Weight Rule.** The most consequential fact gets the first position and the strongest treatment. The summary grid is sorted by consequence, not declaration order: the two verdict cards (Critical, Warnings) lead and hold their position **even at zero**, because "no criticals" is itself the headline finding; inventory cards follow in triage relevance (Volumes, Physical Disks, NVMe, Thunderbolt, USB, Network); and any inventory card reporting `0` sinks below those with data, so an empty "USB 0" never sits between the reader and the verdict. The Critical card additionally takes a tinted fill, colored border, and status-colored number only when non-zero. Ordering lives in `Render.Html.byConsequence` and is covered by tests.

Scale now participates too: the Critical card spans two grid columns when non-zero (`@media (min-width:480px)`, guarded so it can never span more columns than the grid has), and collapses back to a single cell at zero. Position, color, and width all track consequence.

## 4. Elevation

Hybrid, and strictly so. Surfaces are **flat at rest**: structure is carried by 1px hairline borders and the tonal step between the page ground and panel white. Shadow is not a ranking device and never signals importance at rest. It appears only as a **response to state**, which keeps the resting page calm enough that hierarchy has to be earned through position, size, and weight rather than bought with depth.

### Shadow Vocabulary
- **Ambient panel** (`box-shadow: 0 1px 3px rgba(0,0,0,.08)` light / `rgba(0,0,0,.4)` dark): The single resting shadow, applied to cards, disclosure panels, and the sticky header. Its job is to separate a panel from the ground by a hair, not to lift it.
- **Focus ring** (`outline: 2px solid {colors.accent}; outline-offset: 2px`): Applied via `:focus-visible` to every interactive element. This is elevation-as-state in its purest form and is non-negotiable.

### Named Rules

**The State-Only Depth Rule.** At rest, everything is flat. Shadow, outline, and background shift are reactions to hover, focus, or expansion. If a surface needs more prominence at rest, raise its position or its type weight, never its shadow.

**The Hairline-First Rule.** Structure is a 1px border before it is anything else. If a new component needs separation, the hairline is the first tool reached for and the shadow is the last.

## 5. Components

Dense and instrument-like. Controls recede until needed; data has presence.

### Buttons
- **Shape:** Gently rounded (8px) for chrome controls; the in-table sort control is unstyled and inherits the cell.
- **Theme toggle:** Page-ground fill on panel white, hairline border, ink text, 8px 13px padding.
- **Hover / Focus:** Border shifts to Signal Blue on hover. Focus shows the 2px accent ring at 2px offset, always visible, never suppressed.
- **Sort control:** A real `<button>` filling the header cell (`all: unset`, 100% width, 10px 13px padding). It is a button so it is natively focusable and Enter/Space-operable; the `<th>` retains `scope="col"` and carries `aria-sort` state. Never revert this to a click handler on a bare `<th>`.

### Cards / Containers
- **Corner Style:** 12px on cards and disclosure panels.
- **Background:** Panel white on the cool field ground.
- **Shadow Strategy:** Ambient panel only. See Elevation.
- **Border:** 1px hairline, always present. The border, not the shadow, is what defines the edge.
- **Internal Padding:** 16px on cards; 14px 18px on disclosure summaries.

### Inputs / Fields
- **Style:** Page-ground fill, 1px hairline, 8px radius, 8px 12px padding, 220px min-width.
- **Focus:** 2px accent outline at 2px offset.
- **Behavior:** The header search filters every table row on the page simultaneously.

### Navigation
There is no navigation. The report is a single scrolling document with a sticky header carrying identity, the global filter, and the theme toggle. Disclosure panels are the only structural affordance. **Do not add a sidebar**; it would import the exact enterprise-monitoring shell this system rejects.

### Disclosure Panel (signature)
Every section is a real `<details open>` / `<summary>` pair. The summary shows an icon, the section title, and a muted count; a `▸` glyph rotates 90° on open via `details[open]`. Native `<details>` is chosen deliberately: it is keyboard-operable and screen-reader-announced for free, and it degrades to fully-expanded content when JavaScript is unavailable. **Never reimplement this with `<section>` plus a click handler** — that exact bug shipped once here, silently disabling every disclosure on the page.

### Summary Card (signature)
A metric tile carrying icon, value, label, and a sub-line of context. Cards that map to a rendered panel are **real `<button>`s** (`.card-live`) with `aria-pressed`; internals are `<span>`s because a button may only contain phrasing content. Activating a card enters **focus mode**: its panel opens, every other `details[data-section]` closes, and the page scrolls to it (`behavior:'auto'` under `prefers-reduced-motion`). Activating the focused card again restores the full report, and manually expanding any panel exits focus mode so `aria-pressed` never claims a filter that is no longer in effect.

The two severity cards additionally **narrow the Warnings panel to their own severity** via `data-filter`, and an active filter is announced with a chip ("showing critical only") so a shortened list is never misread as missing data.

A card whose activation would show the reader nothing of its own stays a plain `<div>`: a category that rendered no panel (an empty USB list), and a severity card at zero. **Never render a control that does nothing, or that promises one thing and shows another** — clicking "Advisories 0" once opened a panel of five findings, none of them advisories. A dead or lying button is worse than no affordance.

Focus mode is safe precisely because summary chips live in the `<summary>`: a collapsed panel still shows its severity and totals, so isolating one section never hides the verdict.

**The Warnings/Advisories Split.** The card labelled **Advisories** counts `Severity = Warning` only; the panel titled **Warnings** counts every finding at any severity. They were both called "Warnings" and read as a contradiction ("Warnings 0" beside "Warnings (5)"). Keep the two names distinct.

### Summary Chip (signature)
A pill (999px, 0.72rem/600) carried in the `.sumbar` of a disclosure summary and in card sub-lines. Severity variants use a soft fill with a **darkened ink** (`--crit-ink` `#991b1b`, `--warn-ink` `#92400e`, `--ok-ink` `#166534`) rather than the raw status color, because 11.5px text on a soft tint does not clear 4.5:1 against the undarkened hue. Measured: critical chip 7.6:1.

Every severity chip carries **glyph + number + word** ("🔴 3 critical"), which is what satisfies the Never-Hue-Alone Rule. Because chips live in the `<summary>`, severity stays readable while the panel is collapsed — a section can never hide a critical count behind a disclosure.

### Column Aggregate (signature)
A muted 0.7rem line beneath a column label inside the `<th>`, deliberately **outside** the sort `<button>` so the control's accessible name stays just the column label. Only rendered where the aggregate is honest: whole-disk sizes are additive and get a real total, while volume and `df` columns get worst-offender or at-risk counts instead, because APFS volumes share a container and summing them multiplies one disk by its volume count.

### Capacity Bar (signature)
A 120px × 18px track (page-ground fill, hairline border, 6px radius) with a status-colored fill and the percentage centered in 0.72rem/600 micro type using `mix-blend-mode: difference` so it stays legible against both filled and empty regions. The number inside the bar is what satisfies the Never-Hue-Alone Rule; it is not optional ornamentation.

## 6. Do's and Don'ts

### Do:
- **Do** lead with the verdict. Warnings render above the inventory and stay above it. Position is the primary hierarchy device in this system.
- **Do** pair every status color with a glyph, word, or number. Green/amber/red alone is a defect, not a style choice.
- **Do** use a real `<details>`/`<summary>` for any collapsible region, and a real `<button>` for any activatable control. Native semantics are the accessibility strategy.
- **Do** keep body text at or above 4.5:1 against its surface, and large text at or above 3:1 (WCAG 2.2 AA, per PRODUCT.md).
- **Do** show absence as `-` when a figure is unknown. Per PRODUCT.md's *Honest numbers or none*: a blank cell is correct, a fabricated 100% is a bug that already shipped here once.
- **Do** define structure with 1px hairlines before reaching for shadow.
- **Do** keep the type scale fixed in rem. Structural responsiveness only.

### Don't:
- **Don't** build enterprise monitoring clutter, in the Grafana and Nagios mold: density without hierarchy, every panel the same visual weight, nothing prioritized, the operator left doing the ranking the tool should have done. This is PRODUCT.md's named anti-reference and the single thing this system exists to avoid.
- **Don't** use `border-left` or `border-right` greater than 1px as a colored accent stripe on cards, rows, or callouts. **The warning list currently violates this** with a 4px left stripe; it is a known deviation to be resolved, not a pattern to copy.
- **Don't** ship uniform card grids of identical weight. Largely resolved: cards are sorted by consequence, the Critical card leads and earns an alert treatment when non-zero, and the cards that orphan onto a second row are now the inert zeros rather than the verdict. Still open: every card is the same size, so hierarchy rests on position and color alone.
- **Don't** reorder cards on a signal that flips run to run. Consequence rank is fixed per category; only the "has data / is empty" bucket is dynamic. A layout that reshuffles unpredictably between runs costs more than it gains.
- **Don't** count pseudo-filesystems toward a verdict. `devfs` is 100% full by definition and Time Machine snapshot mounts report snapshot capacity; including them once inflated "at risk" from 4 to 7. They stay listed in the table but never in a count. See `Stats.isPseudoFs`.
- **Don't** sum a column just because it holds numbers. Volume and `df` size/used columns are non-additive (shared APFS containers); a total there is a fabricated figure.
- **Don't** use gradient text, `background-clip: text`, or decorative glassmorphism anywhere.
- **Don't** add a sidebar, a top nav, or a dashboard shell. The report is one document.
- **Don't** let the status trio drift into decorative or category use. See The Verdict Rule.
- **Don't** animate anything that does not communicate state. Transitions stay at 150–250ms and every one needs a `prefers-reduced-motion` alternative.
- **Don't** introduce `clamp()` typography or a second font family without a specific, argued reason.

**Audit test:** if you can screenshot the page in grayscale and still identify every failing disk, the color system is working. If you cannot, the Never-Hue-Alone Rule has been broken somewhere.
