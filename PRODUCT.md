# Product

## Register

product

## Platform

web

## Users

IT staff and sysadmins auditing storage on Macs they don't use every day. The defining constraint is unfamiliarity: the reader is sitting at, or remoting into, a machine whose hardware, volume layout, and mount history they didn't set up. They are fluent in disks, APFS, and enclosures, but they know nothing about *this* box. Everything on screen has to orient a competent stranger in seconds.

The job is triage. A successful open means landing on the report and immediately knowing which disk is nearly full, failing, or missing, and what to act on. Completeness matters, but it is in service of that first read, not a substitute for it.

## Product Purpose

MacDiskExplorer inventories macOS storage by correlating sources that normally have to be read separately: `diskutil` plists, `system_profiler` USB/Thunderbolt/NVMe topology, `df`, and `mount`. It resolves them into one typed model and emits console, HTML, JSON, CSV, and Markdown reports, plus history snapshots that diff run to run.

Design effort is scoped to two surfaces: the **HTML dashboard** (`disk-report.html`) and the **Markdown report** (`report.md`). JSON and CSV are machine formats and are exempt. The console report is functional, not a design target. The output directory keeps only the latest copy of each report so a bookmark stays valid, while every run is archived under `history/` with a timestamped name, retaining the 50 most recent instances of each type.

One structural note for anyone acting on this document: the dashboard is not a normal web codebase. Its markup, CSS, and JavaScript are authored as string constants inside `MacDiskExplorer.fsx` (`Render.Html.cssBlock`, `jsBlock`, and the row builders). Design changes are edits to F# string literals, and the generated `disk-report.html` is disposable output that is overwritten on every run.

## Positioning

The disk report that tells you what is wrong before it tells you what is there.

## Brand Personality

An editorial report, not a readout. It should read like a briefing someone wrote for you: strong hierarchy, generous type, a clear order of importance. The voice is precise, calm, and unexaggerated, and it earns trust by never overstating what it knows. Three words: exact, structured, unhurried.

## Anti-references

Enterprise monitoring clutter, in the Grafana and Nagios mold: density without hierarchy, where every panel carries the same visual weight, nothing is prioritized, and the operator is left to do the ranking the tool should have done. Volume of information is not the problem; undifferentiated information is.

## Design Principles

**Lead with the verdict.** The report opens with what is wrong, not with a device tree. Warnings and at-risk volumes outrank the complete inventory in both position and weight. A reader who leaves after five seconds should still leave with the finding.

**Hierarchy is the product.** The named anti-reference fails by making everything equally loud. Every element earns its weight relative to the triage task, and anything that cannot justify its prominence gets demoted.

**Honest numbers or none.** This codebase has already shipped one bug where APFS container arithmetic reported every volume as 100% full and produced twenty false criticals. Where a figure is unknown or untrustworthy, show its absence rather than a confident wrong number. Fabricated certainty is worse than an empty cell.

**Orient a stranger.** The reader did not configure this machine. Device identifiers, units, bus types, and mount points must be self-explanatory without prior knowledge of the host.

**Status never rides on color alone.** Health and severity are always carried by text, shape, or number in addition to hue, so the report survives both color blindness and a monochrome print.

## Accessibility & Inclusion

WCAG 2.2 AA. Body text meets 4.5:1 against its background and large text meets 3:1. Status is never conveyed by hue alone: red/amber/green health indicators must always pair with a label, icon shape, or percentage.

Full keyboard operability is required. Sortable column headers, collapsible sections, and the search filter must all be reachable and operable without a mouse, with a visible focus indicator on every interactive element.
