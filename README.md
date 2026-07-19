# MacDiskExplorer

A macOS storage inventory and diagnostics tool. It correlates the storage information macOS scatters across `diskutil`, `system_profiler`, `df`, and `mount` into one typed model, then emits a report that **leads with what is wrong** rather than with a device tree.

A single self-contained F# script. No project file, no NuGet packages, no build step.

```bash
dotnet fsi MacDiskExplorer.fsx --all
```

## Requirements

- macOS (the tool shells out to native macOS utilities)
- .NET SDK providing `dotnet fsi` — developed and tested on 10.0.302

No other dependencies. The script uses only the .NET base class library.

## Usage

```bash
dotnet fsi MacDiskExplorer.fsx [options]
```

| Option | Effect |
|---|---|
| `--console` | Print an ASCII report to the terminal (default) |
| `--html` | Write `disk-report.html`, a self-contained dashboard |
| `--json` | Write `inventory.json`, the full structured inventory |
| `--csv` | Write `disks.csv`, a flattened disk/volume view |
| `--markdown` | Write `report.md` |
| `--all` | Produce every output above |
| `--out <dir>` | Write outputs and the archive under `<dir>` (default `.`) |
| `--watch` | Refresh the console report every 5s until Ctrl+C |
| `help` | Show usage |

Both dash forms work (`-html` and `--html`).

> **The `.fsx` extension is required.** `dotnet fsi MacDiskExplorer …` will not run the
> script — `fsi` only treats an argument as a script when it ends in `.fsx`, and will
> otherwise try to parse it as one of its own options.

## Output layout

The output directory holds only the latest, unstamped copy of each report, so a bookmark or an open browser tab always points at the current run. Every run is additionally archived with a timestamped name, keeping the 50 most recent instances **of each report type**.

```
.
├── disk-report.html          # latest
├── inventory.json
├── disks.csv
├── report.md
└── history/
    ├── disk-report-20260719-184724.html
    ├── inventory-20260719-184724.json
    ├── disks-20260719-184724.csv
    └── report-20260719-184724.md
```

Every file from a single run shares one timestamp — the collection time — so a run's outputs sort together and match the "Generated" time printed inside the report.

Generated reports are machine-specific (host name, volume names, mount paths, device identifiers) and are excluded from version control by `.gitignore`.

## The HTML dashboard

Self-contained: no CDN, no external stylesheet, no fonts to fetch. Open the file anywhere.

- **Summary cards ordered by consequence.** The Critical card leads, spans two columns and takes an alert treatment when non-zero. Inventory categories with nothing to report sink below those with data.
- **Click a card to focus its panel.** Other sections collapse; click again to restore. The severity cards additionally filter the warnings list to their own severity, and announce the active filter so a shortened list is never misread as missing data.
- **Collapsed panels keep their verdict.** Severity counts, totals, and worst-offender figures live in the panel header, so collapsing a section never hides its findings.
- **Keyboard operable throughout.** Native `<details>`/`<summary>` disclosures and real `<button>` sort controls carrying `aria-sort`, with visible focus rings.
- **Dark and light themes**, defaulting to the OS preference and persisted per browser.
- **Filter box** that narrows every table at once, and sortable columns.

## Data sources

| Source | Used for |
|---|---|
| `diskutil list -plist` | Whole disks, partitions, APFS volumes |
| `diskutil info -plist <dev>` | Media name, bus protocol, SMART, internal/external, SSD |
| `system_profiler SPUSBDataType -json` | USB topology and link speeds |
| `system_profiler SPThunderboltDataType -json` | Thunderbolt devices |
| `system_profiler SPNVMeDataType -json` | NVMe model, capacity, TRIM |
| `df -k` | Authoritative live capacity for mounted volumes |
| `mount` | Network mount detection (NFS, SMB, AFP, WebDAV) |
| `scutil`, `sw_vers`, `sysctl` | Host name, macOS build, hardware model for the report header |

## Warnings

Derived automatically: volumes at or above 90% (critical at 95%), SMART failures, TRIM disabled, USB 2.0 or slower devices, read-only mounts, missing external backup target, and network mounts worth verifying.

## Correctness notes

Three decisions that are easy to get wrong, and that the tests pin down:

**`df` is authoritative for mounted-volume usage, not `diskutil`.** For APFS volumes `diskutil info` reports *container-wide* free space. Taking it at face value makes every volume read as 100% full and produces a screenful of false criticals.

**Totals are only computed where the figures are additive.** Whole-disk capacities are distinct devices and sum correctly. Volume and `df` rows do not: every APFS volume in a container reports that container's size, so summing them multiplies one disk by its volume count. Those columns get worst-offender and at-risk counts instead of a fabricated total.

**Pseudo-filesystems are excluded from verdicts but still listed.** `devfs` is 100% full by definition and Time Machine snapshot mounts report snapshot capacity. Counting them inflated "at risk" from 4 to 7 — a report that cries wolf gets ignored.

The guiding rule, from [PRODUCT.md](PRODUCT.md): where a figure is unknown or untrustworthy, show its absence rather than a confident wrong number.

## History and diffing

Each run writes a JSON snapshot and compares against the previous one, reporting new disks, removed devices, and capacity changes.

```
🕓 History:
   🟢 New disk since last run: disk12
   🔴 Removed disk since last run: disk7
   💾 Archived to ./history/inventory-20260719-184724.json
```

## Architecture

```
Shell → Plist / Jsonp → Collectors → Correlate → Stats → Warnings
                                                            ↓
        Console · Html · Json · Csv · Markdown ← Render → History → Cli
```

Everything lives in [`MacDiskExplorer.fsx`](MacDiskExplorer.fsx) (~2,200 lines) as `module` blocks with an immutable model. Collectors are independent: each runs a command, parses defensively, and contributes to the shared inventory, so a missing or failing tool degrades gracefully instead of aborting the run.

The dashboard's HTML, CSS, and JavaScript are authored as string constants inside the script (`Render.Html.cssBlock`, `jsBlock`). Design changes are edits to F# string literals; the generated HTML is disposable output.

## Tests

```bash
dotnet fsi MacDiskExplorer.Tests.fsx
```

81 assertions, no test framework required. Covers the plist and JSON parsers, `df` and `mount` parsing, correlation, warning thresholds, byte formatting, CSV injection escaping, archive retention, and the report's card ordering and focus targets.

The suite `#load`s the main script; an entry-point guard keeps that from triggering a live system scan.

## Project context

| File | Contents |
|---|---|
| [PRODUCT.md](PRODUCT.md) | Users, purpose, positioning, design principles, accessibility bar |
| [DESIGN.md](DESIGN.md) | The visual system: color roles, type scale, elevation, components |
| [CLAUDE.md](CLAUDE.md) | Orientation for AI agents working in this repo |
