# MacDiskExplorer

A single-file F# script that inventories macOS storage and emits console, HTML, JSON, CSV, and Markdown reports.

## Commands

```bash
dotnet fsi MacDiskExplorer.fsx --all              # every output
dotnet fsi MacDiskExplorer.fsx --html --out docs  # selected outputs, custom directory
dotnet fsi MacDiskExplorer.Tests.fsx              # 35 unit tests, no dependencies
```

The `.fsx` extension is required — `dotnet fsi MacDiskExplorer …` will not run the script, because `fsi` only treats an argument as a script when it ends in `.fsx`. Flags accept either dash form (`-html` and `--html` both work).

## Architecture

`Shell` → `Plist` / `Jsonp` (parsers) → `Collectors` → `Correlate` → `Warnings` → `Render` (Console/Html/Json/Csv/Markdown) → `History` → `Cli`. Everything lives in `MacDiskExplorer.fsx`; there is no project file and no NuGet dependency.

Two non-obvious rules learned the hard way:

- **`df` is authoritative for mounted-volume usage, not `diskutil`.** For APFS volumes `diskutil info` reports container-wide free space, which once made every volume read as 100% full and produced 20 false criticals. `Correlate.enrichVolumesWithDf` overrides usage from `df`; unmounted volumes correctly report no capacity rather than a fabricated one.
- **The entry point is guarded** on the invoked script name so `MacDiskExplorer.Tests.fsx` can `#load` the module without triggering a full system collection.
- **`system_profiler` data-type names are not stable across macOS versions.** macOS 26 replaced `SPUSBDataType` with `SPUSBHostDataType` and renamed every property; the old name returns an empty result rather than erroring, so devices vanish silently. `Collectors.collectUsb` queries the current type first, falls back to the legacy one, and accepts either key vocabulary via `strAny`. Apply the same pattern before trusting any new `SP*DataType`.
- **Link speed belongs to the physical disk only.** A synthesized APFS container reports the same `MediaName` as its physical store, so `Correlate.attachLinkSpeed` skips disks where `VirtualOrPhysical` is `Virtual` to avoid reporting one connection twice.

## Design Context

Strategic and visual context live in two root files. Read both before any UI work:

- **[PRODUCT.md](PRODUCT.md)** — register (`product`), platform (`web`), users, purpose, positioning, anti-references, design principles, accessibility bar.
- **[DESIGN.md](DESIGN.md)** — the visual system: color roles, type scale, elevation, components, and Do's/Don'ts. Machine-readable tokens are in its YAML frontmatter; extended metadata (tonal ramps, motion, component snippets) is in `.impeccable/design.json`.

**The dashboard is not a normal web codebase.** Its HTML, CSS, and JavaScript are authored as string constants inside `MacDiskExplorer.fsx` (`Render.Html.cssBlock`, `jsBlock`, and the row builders). Design changes are edits to F# string literals. The generated HTML is disposable output — never edit it directly, and never inject anything into it expecting the change to survive a regeneration.

**Output layout.** The output directory holds only the unstamped *latest* of each report (`disk-report.html`, `inventory.json`, `disks.csv`, `report.md`), so a bookmark or open browser tab always points at the current run. Every run is additionally archived to `history/` as `<name>-YYYYMMDD-HHmmss.<ext>` (`Cli.stampName`), and the archive is capped at `History.retentionCap` (50) instances **per report type** — pruning is per type, never a global cap. Every file from a run shares the collection timestamp, so a run's outputs sort together and match the "Generated" time inside the report.

The JSON inventory is archived by `History.save` (needed for run-to-run diffing even when `--json` was not requested), so `writeOutputs` deliberately does not archive it a second time.

Design effort is scoped to the HTML dashboard and the Markdown report. JSON and CSV are machine formats; the console report is functional, not a design target.

Non-negotiables carried from PRODUCT.md: lead with the verdict, never convey status by hue alone, keep the surface keyboard-operable with native `<details>`/`<button>` semantics, and show absence rather than a confident wrong number.
