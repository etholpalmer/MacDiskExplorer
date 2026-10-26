# Lessons Learned

> Engineering journal for MacDiskExplorer — what was learned, what went wrong,
> and what tradeoffs were made. Each entry stands alone; skim the headers.

## Environment

- **OS:** macOS 26.5.2 (25F84), ARM64 (Apple Mac15,6)
- **Runtime:** .NET SDK 10.0.302, `dotnet fsi` (F# Interactive)
- **IDE:** none — single `.fsx` script, edited directly
- **Key tools:** native macOS CLIs (`diskutil`, `system_profiler`, `df`, `mount`, `scutil`, `sw_vers`, `sysctl`); no NuGet, no project file
- **Last Updated:** 2026-10-25

---

## Lessons

### 2026-07-19 — `df` is authoritative for mounted-volume usage, not `diskutil`

**Insight:** For an APFS volume, `diskutil info` reports the *container's* free space, not the volume's. Computing `used = size - free` from it makes every volume in a shared container read as ~100% full. `df -k` reports true per-volume live usage.

**Discovery:** First `--console` run showed all 20 volumes at 100% and emitted 22 "critical" capacity warnings. `df -k /` said 66%.

**Design intent:** The original collector trusted a single source (`diskutil`) for everything, assuming "free space" meant the same thing at the volume layer as at the device layer. APFS's shared-container model breaks that assumption one layer down.

**Impact:** Domain truth, permanent. Any macOS storage tooling must treat `df` as the source of mounted-volume usage and treat `diskutil` free space as container-level only.

**Active mitigation:** `Correlate.enrichVolumesWithDf` overrides usage from `df` by mount point; `volumeFromInfo` deliberately sets used/avail/percent to `None` so unmounted volumes report absence rather than a fabricated figure.

**Related:** Mistakes "False 100%-full volumes"; Lesson "Honest numbers or none".

---

### 2026-07-19 — Honest numbers or none (show absence, never a confident wrong number)

**Insight:** A blank cell beats a fabricated figure. Three separate features tried to compute a number that was wrong-but-plausible, each of which would have misled a triage read.

**Discovery:** Emerged across three bugs: APFS false-100%, column totals summed across shared containers (one 460 GB disk → "6 TB"), and pseudo-filesystem verdict inflation (devfs + Time Machine snapshots pushed "at risk" from 4 to 7).

**Impact:** Permanent design principle, recorded in PRODUCT.md. Prefer `-` / omission over a derived number whose inputs don't actually support it.

**Active mitigation:** `Stats.totalDiskBytes` sums whole disks only (additive); volume/`df` columns get worst-offender counts instead of totals. `Stats.isPseudoFs` excludes devfs and TM snapshots from verdict counts while still listing them. Unknown capacity renders `-`.

**Related:** Lesson "df authoritative"; Mistakes "Inflated filesystem verdict", "Non-additive column totals".

---

### 2026-10-25 — `system_profiler` data-type names drift across macOS versions (and fail silently)

**Insight:** macOS 26 replaced `SPUSBDataType` with `SPUSBHostDataType` and renamed every property (`speed` → `USBDeviceKeyLinkSpeed`). Querying a retired data-type name returns an **empty result, not an error**, so devices silently vanish from output with no failure signal.

**Discovery:** USB card read "0" while four USB disks were physically attached; the "USB 2.0 is slow" warning could therefore never fire. `system_profiler -listDataTypes` showed `SPUSBHostDataType`, not `SPUSBDataType`.

**Design intent:** The collector named the data type that was current when it was written, assuming those names are stable API. They are versioned surface, not a contract.

**Impact:** Pattern for any `SP*DataType`: query the current name first, fall back to the legacy one, and accept either key vocabulary. Empty ≠ absent; it may mean wrong name.

**Active mitigation:** `Collectors.firstPopulated [current; legacy]` + `strAny [newKey; oldKey]`. Documented in CLAUDE.md as a standing rule before trusting any new `SP*DataType`.

**Related:** Mistakes "USB devices silently vanished on macOS 26".

---

### 2026-08-12 — Behavioral verification catches what markup/grep verification misses

**Insight:** Checking that generated markup *contains* the right strings proves nothing about whether it *works*. Two bugs passed a grep/structure check and were only caught by driving the rendered page in a browser.

**Discovery:** Promoted to a first-class lesson after the technique caught its second bug. The cheap check (grep for `<details>`, `class="chip"`) was green in both cases; the behavior was broken.

**Bug categorisation (what would have caught each cheapest):**

| Bug | Grep/markup check | Behavioral (browser) check |
|---|---|---|
| `<section open>` used instead of `<details>` — disclosure never toggled | ❌ markup looked fine | ✅ measured panel height 1161→1161px (no collapse) |
| Hidden filter chip rendered as empty pill (`.chip{display:inline-flex}` beat UA `[hidden]`) | ❌ element present as expected | ✅ `getComputedStyle().display` still `inline-flex` when hidden |
| Sort headers not keyboard-operable | partial (could see bare `<th>`) | ✅ confirmed focus + `aria-sort` flip |

**Impact:** For generated UI, verify the *behavior* (toggle, focus, computed style, contrast ratio) not the *presence* of markup. A separate gotcha surfaced here too: `<details>` collapses via content-visibility, so computed `display` stays `block` while collapsed — **height**, not `display`/`offsetParent`, is the authoritative collapse signal.

**Related:** Mistakes "Disclosure never worked", "Hidden chip empty pill", "Sort headers not keyboard-operable".

---

### 2026-07-19 — A synthesized APFS disk shares its physical store's MediaName

**Insight:** `diskutil` lists a synthesized APFS container (e.g. `disk7`) as a whole disk with the **same `MediaName`** as the physical store beneath it (`disk6`, "PSSD T7"). Correlating external data by media name alone tags both and double-reports one physical connection.

**Discovery:** When attaching USB link speed, both `disk6` and `disk7` matched "PSSD T7"; `disk7` has no connection of its own.

**Impact:** `VirtualOrPhysical` = `Virtual` distinguishes synthesized from physical. Correlate connection-level facts to physical disks only.

**Active mitigation:** `Correlate.attachLinkSpeed` skips disks where `Virtual = Some true`; the field is carried on the model.

**Related:** Mistakes "Link-speed double-attribution".

---

## Mistakes & Fixes

### 2026-07-19 — False 100%-full volumes / 22 phantom criticals

**Symptom:** Every volume reported ~100% capacity; 22 critical warnings on first run.

**Root Cause:** `used = TotalSize − FreeSpace` using `diskutil`'s container-wide free space for APFS volumes.

**Fix:** `volumeFromInfo` stopped deriving usage from `diskutil`; `Correlate.enrichVolumesWithDf` fills it from `df -k` by mount point (`MacDiskExplorer.fsx`).

**Prevention:** Treat `df` as authoritative for mounted usage.

**Time Lost:** ~15 min (caught on first verification run).

**Severity:** High — would have made the headline output useless.

**Related:** Lesson "df authoritative".

---

### 2026-08-12 — Collapsible sections never actually collapsed

**Symptom:** Every dashboard panel was permanently expanded; the disclosure caret didn't rotate.

**Attempted:** Earlier verification grepped the output for the CSS/JS and confirmed they were embedded — markup looked correct, so the bug shipped. Caught later by a browser behavioral test (see Lesson "Behavioral verification").

**Root Cause:** Markup emitted `<section open><summary>…</summary></section>`. `<summary>` is only valid inside `<details>`, and `open` is inert on `<section>`. Zero `<details>` elements existed.

**Fix:** Switched section/warnings/raw panels to real `<details open>`; verified toggle in-browser (1161px → 52px collapsed).

**Prevention:** Behavioral verification for generated UI, not markup presence.

**Time Lost:** ~20 min.

**Severity:** High — a spec'd feature shipped completely non-functional and passed a false-positive check.

**Related:** Lesson "Behavioral verification"; Mistakes "Sort headers not keyboard-operable".

---

### 2026-10-25 — USB devices silently vanished on macOS 26

**Symptom:** USB card "0" with four USB SSDs attached; slow-USB warning unreachable.

**Attempted:** Assumed the empty result meant "no USB devices" — initially explained the disk-image churn as simulator runtimes (wrong guess, corrected by inspecting `hdiutil info`).

**Root Cause:** `SPUSBDataType` retired in macOS 26 → empty JSON, no error.

**Fix:** `collectUsb` queries `SPUSBHostDataType` first, falls back to `SPUSBDataType`; `strAny` accepts both key vocabularies (commit `5089438`).

**Prevention:** Empty `system_profiler` result ≠ no devices; may be a renamed data type.

**Time Lost:** ~25 min including the wrong-guess detour.

**Severity:** High — an entire device class missing, and a warning that could never fire.

**Related:** Lesson "system_profiler data-type names drift".

---

### 2026-07-19 — Sort headers not keyboard-operable

**Symptom:** Column sorting worked by mouse only; headers not focusable, no `aria-sort`.

**Root Cause:** Click handlers on bare `<th>`.

**Fix:** Real `<button>` inside each `<th scope="col">`, `aria-sort` reflecting state, visible focus ring; verified focus + activation in-browser.

**Prevention:** Interactive = native control (`<button>`), not a click handler on a non-interactive element — a PRODUCT.md accessibility non-negotiable.

**Severity:** Medium — accessibility violation against a stated requirement.

**Related:** Lesson "Behavioral verification".

---

### 2026-10-25 — Link-speed double-attribution to synthesized disks

**Symptom:** Risk of reporting one USB connection on both the physical disk and its synthesized APFS container.

**Root Cause:** Shared `MediaName` between physical and synthesized disks.

**Fix:** `attachLinkSpeed` skips `Virtual = Some true`; verified `disk6`=10 Gb/s, `disk7`=`-`.

**Severity:** Medium — would have fabricated a second connection.

**Related:** Lesson "Synthesized APFS disk shares MediaName".

---

### 2026-10-25 — Inflated filesystem verdict (pseudo-filesystems counted)

**Symptom:** "7 critical" filesystems; inspection showed `devfs` (100% by definition) and two Time Machine snapshot mounts among them.

**Root Cause:** Verdict counted every `df` row ≥90%, including pseudo/snapshot mounts the operator can't act on.

**Fix:** `Stats.isPseudoFs` excludes devfs, `map`, TM snapshots, `/dev`, `.timemachine` from verdict counts; they stay listed in the table. Honest count = 4.

**Prevention:** Separate "listed in inventory" from "counted toward a verdict".

**Severity:** Medium — a report that cries wolf gets ignored.

**Related:** Lesson "Honest numbers or none".

---

### 2026-07-19 — `Shell.run` timeout was illusory; latent stderr deadlock

**Symptom:** None observed (latent). Flagged in self-review.

**Root Cause:** `StandardOutput.ReadToEnd()` blocks before `WaitForExit(timeout)` is reached, so the timeout never fires; draining stdout then stderr sequentially can deadlock if a child fills the stderr pipe buffer.

**Fix:** Concurrent `ReadToEndAsync` on both streams + real `WaitForExit(timeoutMs)` + `Kill(true)` on timeout.

**Prevention:** Never read one captured stream to completion before the other; always bound external processes with an enforced deadline.

**Severity:** Medium — latent hang risk; the specific macOS CLIs are well-behaved, so it never triggered.

---

### 2026-10-25 — "Advisories 0" card opened a panel of non-advisories

**Symptom:** Clicking the (zero-count) Advisories card opened the Warnings panel showing 5 rows — 3 critical + 2 info, zero advisories.

**Attempted:** Assumed the card already "linked correctly" because it had a `data-target`; caught by actually clicking it in-browser.

**Root Cause:** Severity cards targeted the whole panel without filtering, and remained live controls at zero count.

**Fix:** Severity cards carry `data-filter`; JS narrows the list to that severity and announces the active filter; cards go inert (`<div>`, no target) at zero count — same "no dead controls" rule as empty categories.

**Prevention:** A control must show what it promises; a zero-count filter control is a broken promise, make it inert.

**Severity:** Medium.

**Related:** Lesson "Behavioral verification".

---

### 2026-08-12 — Warning-row side stripe violated the project's own design ban

**Symptom:** Warning rows used `border-left: 4px` as a severity accent — a pattern explicitly banned in DESIGN.md, written by the same author who set the ban.

**Root Cause:** Reflexive side-stripe styling.

**Fix:** Full 1px border + soft severity tint; severity still carried by glyph + bold category word. Body text measured 14.8:1 (commit `dd8fceb`).

**Prevention:** Grep generated output for `border-left`/`border-right` > 1px before shipping UI.

**Severity:** Low — cosmetic, but a self-consistency miss.

**Related:** Mistakes "Hidden chip empty pill".

---

### 2026-10-25 — Hidden filter chip rendered as an empty pill

**Symptom:** A stray empty pill before the warning summary chips.

**Root Cause:** Author rule `.chip{display:inline-flex}` overrode the UA `[hidden]{display:none}` rule (author beats UA regardless of specificity).

**Fix:** Explicit `.chip[hidden]{display:none}`; confirmed `display:none`, width 0.

**Prevention:** When styling `display` on an element that may carry `[hidden]`, restore the hidden rule explicitly.

**Severity:** Low.

**Related:** Lesson "Behavioral verification" (author-vs-UA CSS gotcha).

---

## Solution Gaps

### 2026-10-25 — History diff announces disk-image churn as "new disk"

**Current State:** The run-to-run diff reports any new block device as "🟢 New disk".

**Limitation:** macOS attaches/detaches disk images constantly (installer DMGs, simulator runtimes, sparse images). A mounted DaVinci Resolve installer DMG and iOS Simulator runtime images were each flagged as "new disk" though no hardware changed.

**Ideal Solution:** Exclude `BusProtocol = "Disk Image"` and `Virtual = true` devices from the diff (still list them in the inventory), mirroring the pseudo-filesystem verdict exclusion.

**Closing this gap requires:**
1. Filter in `History.diffAgainstPrevious` for disk-image/virtual devices — ~15 min — pending
2. A test with fixture snapshots proving image churn is suppressed — ~15 min — pending

**Priority:** Medium — it's the history equivalent of the pseudo-fs verdict noise already fixed elsewhere.

**Related:** Lesson "Honest numbers or none".

---

### 2026-10-25 — Thunderbolt link speed path untested

**Current State:** Link-speed correlation wired for USB; Thunderbolt column present but always `-`.

**Limitation:** `current_speed_key` only populates when a Thunderbolt device is attached; none available to test against. The three `thunderboltusb4_bus_*` entries are host controllers with nothing connected.

**Ideal Solution:** Verify against a real Thunderbolt enclosure and extend `attachLinkSpeed` to the Thunderbolt topology.

**Priority:** Low — no current hardware; USB (the actual external disks here) is covered.

---

### 2026-10-25 — Summary cards share one size

**Current State:** Hierarchy carried by position (consequence order), color (Critical alert tint), and width (Critical spans 2 columns when non-zero).

**Limitation:** All eight cards are otherwise the same scale.

**Ideal Solution:** Optional — size is arguably sufficient already; revisit only if the grid reads flat.

**Priority:** Low.

---

### 2026-08-12 — Critical-card click behavior unresolved

**Current State:** Clicking Critical opens + filters-to-critical + collapses other panels (full focus mode).

**Limitation:** User was offered plain-jump vs. jump+filter vs. keep-current and left it at "[No preference]", so current behavior stands by default.

**Ideal Solution:** Confirm intended behavior; the three variants are each a small JS change.

**Priority:** Low.

---

## Compromises

### 2026-07-19 — Hand-rolled assertion harness instead of xUnit/FsUnit

**Tradeoff:** Tests use a 10-line `check`/`checkEq` harness (98 assertions) rather than xUnit + FsUnit (the global testing rule's preference) with coverage tooling.

**Reason:** The spec mandates a single self-contained `.fsx` with zero NuGet dependencies. xUnit requires a project file and packages.

**Impact:** No coverage report, no `dotnet test` integration; assertions are pass/fail counts. Acceptable — the suite is fast, dependency-free, runs via `dotnet fsi`, and `#load`s the script under the entry-point guard.

**Prevention going forward OR accepted recurrence:** Accepted recurrence. The zero-dependency constraint is the point of the project; the harness stays as long as that constraint does.

**Revisit When:** The project ever grows a `.fsproj` for other reasons.

---

### 2026-07-19 — Single 2258-line `.fsx` exceeds the 800-line file guideline

**Tradeoff:** One file holds the entire tool; the common coding-style rule caps files at 800 lines.

**Reason:** Explicit spec constraint — "single `.fsx`, no NuGet". Splitting into modules would require `#load` chains or a project.

**Impact:** Large file; mitigated by clear `module` boundaries (`Shell`→`Plist`→`Collectors`→`Correlate`→`Stats`→`Warnings`→`Render`→`History`→`Cli`) and the dashboard's CSS/JS lifted into named constants.

**Prevention going forward OR accepted recurrence:** Accepted recurrence, driven by the single-file requirement.

**Revisit When:** The single-file constraint is lifted.

---

### 2026-10-25 — CI runs on ubuntu, not macOS

**Tradeoff:** The test workflow uses `ubuntu-latest`; the tool targets macOS.

**Reason:** The suite is pure (fixture strings; the entry-point guard blocks collection), so it's platform-independent, and macOS runners cost ~10x.

**Impact:** CI exercises parsing/correlation/render logic but **not** the live macOS collection path (`diskutil`/`system_profiler` invocation and real-output parsing). Those are verified manually on-device.

**Prevention going forward OR accepted recurrence:** Accepted recurrence. If a macOS-collection regression ever ships undetected by CI, add a minimal `macos-latest` smoke job that runs `--console` and asserts exit 0.

**Revisit When:** A collection-path bug escapes to a user.

---

### 2026-07-19 — Dashboard is authored as F# string literals

**Tradeoff:** The HTML/CSS/JS live as string constants (`Render.Html.cssBlock`, `jsBlock`, row builders) inside the `.fsx`, not as a normal web codebase.

**Reason:** The tool emits a single self-contained HTML file with no external assets; the generator must carry everything inline.

**Impact:** Design changes are edits to F# string literals; no component files, no stylesheet, no bundler. Generated `disk-report.html` is disposable output. Documented in CLAUDE.md so no one edits the output expecting it to persist.

**Prevention going forward OR accepted recurrence:** Accepted recurrence, inherent to "self-contained single HTML file".

**Revisit When:** The output grows enough to justify a real front-end build.

---

## Archive

> Entries moved here when the underlying condition no longer applies.

_(none yet)_
