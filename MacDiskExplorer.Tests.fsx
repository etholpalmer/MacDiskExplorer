(*
    MacDiskExplorer.Tests.fsx
    ===================================================================
    Dependency-free unit tests for the pure logic in MacDiskExplorer.fsx.

    Because the main script guards its entry point on the invoked script
    name, `#load`-ing it here does NOT trigger system collection — the
    modules are simply made available for direct testing.

    Run:
        dotnet fsi MacDiskExplorer.Tests.fsx
    ===================================================================
*)

#load "MacDiskExplorer.fsx"

open System
open System.Text.RegularExpressions

open MacDiskExplorer   // implicit module named after the loaded script
open Model

// ---- Tiny assertion harness (no external test framework) ----
let mutable passed = 0
let mutable failed = 0

let check (name: string) (cond: bool) =
    if cond then passed <- passed + 1
    else
        failed <- failed + 1
        eprintfn "  ❌ FAIL: %s" name

let checkEq (name: string) (expected: 'a) (actual: 'a) =
    check (sprintf "%s — expected %A, got %A" name expected actual) (expected = actual)

// ---- Fixtures / builders ----
let mkVolume id mount pct ro : Volume =
    { DeviceId = id; Name = Some id; MountPoint = mount; FileSystem = Some "APFS"
      SizeBytes = Some 1000L; UsedBytes = None; AvailBytes = None
      CapacityPercent = pct; ReadOnly = ro }

let mkDisk id vols : PhysicalDisk =
    { DeviceId = id; MediaName = Some "Media"; SizeBytes = Some 1000L
      Internal = Some true; SolidState = Some true; BusProtocol = Some "PCI-Express"
      Removable = Some false; SmartStatus = Some "Verified"
      LinkSpeed = None; Virtual = Some false; Volumes = vols }

let mkUsb name speed : UsbDevice =
    { Name = name; Manufacturer = Some "ACME"; Speed = speed
      SerialNumber = Some "SN123"; Vendor = Some "0x04e8" }

let mkInv disks : Inventory =
    { Timestamp = DateTime(2026, 1, 1)
      Computer = { HostName = "test"; Model = None; OsVersion = None }
      Disks = disks; Usb = []; Thunderbolt = []; Nvme = []
      NetworkMounts = []; FsUsage = []; Warnings = [] }

// ===================================================================
printfn "Fmt.bytes"
checkEq "None -> -" "-" (Fmt.bytes None)
checkEq "0 bytes" "0 B" (Fmt.bytes (Some 0L))
checkEq "1024 -> 1.00 KB" "1.00 KB" (Fmt.bytes (Some 1024L))
checkEq "1.5 GB" "1.50 GB" (Fmt.bytes (Some 1610612736L))
checkEq "negative -> -" "-" (Fmt.bytes (Some -5L))

printfn "Fmt.healthIcon"
checkEq "50%% healthy" "🟢" (Fmt.healthIcon (Some 50))
checkEq "92%% warning" "🟡" (Fmt.healthIcon (Some 92))
checkEq "99%% critical" "🔴" (Fmt.healthIcon (Some 99))

printfn "Fmt.classifyBus"
checkEq "USB" ("USB", "🔌") (Fmt.classifyBus (Some "USB") (Some false))
checkEq "Thunderbolt" ("Thunderbolt", "⚡") (Fmt.classifyBus (Some "Thunderbolt") (Some false))
checkEq "internal fallback" ("Internal", "💽") (Fmt.classifyBus None (Some true))

// ===================================================================
printfn "Collectors.parseDf"
let dfSample =
    "Filesystem   1024-blocks      Used Available Capacity iused    ifree %iused  Mounted on\n"
    + "/dev/disk3s5   482797652 449522388   6506532    99% 3312761 65065320    5%   /System/Volumes/Data\n"
    + "/dev/disk9     100000       25000     75000     25% 10 20 1%   /Volumes/My Backup Disk\n"
let dfRows = Collectors.parseDf dfSample
checkEq "two rows parsed" 2 dfRows.Length
let data = dfRows |> List.tryFind (fun r -> r.MountPoint = "/System/Volumes/Data")
check "Data row found" data.IsSome
match data with
| Some r ->
    checkEq "Data size bytes" (Some(482797652L * 1024L)) r.SizeBytes
    checkEq "Data used bytes" (Some(449522388L * 1024L)) r.UsedBytes
    checkEq "Data capacity" (Some 99) r.CapacityPercent
| None -> ()
let spaced = dfRows |> List.tryFind (fun r -> r.MountPoint = "/Volumes/My Backup Disk")
check "mount point with spaces parsed" spaced.IsSome
checkEq "header-only yields empty" 0 (Collectors.parseDf "Filesystem blocks\n").Length

// ===================================================================
printfn "Collectors.parseMounts"
let mountSample =
    "/dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)\n"
    + "//guest@nas._smb._tcp.local/share on /Volumes/share (smbfs, nodev, nosuid)\n"
    + "nas:/export on /Users/x/nfs (nfs, nodev)\n"
    + "map -hosts on /net (autofs, nosuid, automounted)\n"
let nets = Collectors.parseMounts mountSample
checkEq "two network mounts detected" 2 nets.Length
check "smbfs detected" (nets |> List.exists (fun n -> n.FsType = "smbfs"))
check "nfs detected" (nets |> List.exists (fun n -> n.FsType = "nfs"))
check "local apfs excluded" (nets |> List.forall (fun n -> n.MountPoint <> "/"))

// ===================================================================
printfn "Plist.parse + accessors"
let plistSample =
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
    + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">"
    + "<plist version=\"1.0\"><dict>"
    + "<key>VolumeName</key><string>Foo</string>"
    + "<key>TotalSize</key><integer>123456</integer>"
    + "<key>WritableVolume</key><false/>"
    + "</dict></plist>"
match Plist.parse plistSample with
| Some root ->
    checkEq "plist string" (Some "Foo") (Plist.strAt "VolumeName" root)
    checkEq "plist integer" (Some 123456L) (Plist.intAt "TotalSize" root)
    checkEq "plist bool false" (Some false) (Plist.boolAt "WritableVolume" root)
    checkEq "plist missing key" None (Plist.strAt "Nope" root)
| None -> check "plist parsed" false
checkEq "garbage plist -> None" None (Plist.parse "not xml at all")

// ===================================================================
printfn "Jsonp.parse"
match Jsonp.parse "{\"_name\":\"dev\",\"speed\":\"Up to 480 Mb/s\"}" with
| Some el ->
    checkEq "json string prop" (Some "dev") (Jsonp.strProp "_name" el)
    checkEq "json missing prop" None (Jsonp.strProp "missing" el)
| None -> check "json parsed" false

// ===================================================================
printfn "Correlate.enrichVolumesWithDf"
let vol = mkVolume "disk3s5" (Some "/data") None false
let disk = mkDisk "disk3" [ vol ]
let usage : FsUsage list =
    [ { Filesystem = "/dev/disk3s5"; MountPoint = "/data"
        SizeBytes = Some 2000L; UsedBytes = Some 840L; AvailBytes = Some 1160L
        CapacityPercent = Some 42 } ]
let enriched = Correlate.enrichVolumesWithDf usage [ disk ]
let ev = enriched.Head.Volumes.Head
checkEq "df overrides capacity" (Some 42) ev.CapacityPercent
checkEq "df fills used" (Some 840L) ev.UsedBytes

// ===================================================================
printfn "Correlate.attachLinkSpeed"
let usbDevices = [ mkUsb "PSSD T7" (Some "10 Gb/s"); mkUsb "Old Stick" (Some "480 Mb/s") ]
// disk6 is the physical device; disk7 is the synthesized APFS container that
// sits on it and reports the very same MediaName.
let physical = { mkDisk "disk6" [] with MediaName = Some "PSSD T7"; Virtual = Some false }
let synthesized = { mkDisk "disk7" [] with MediaName = Some "PSSD T7"; Virtual = Some true }
let unrelated = { mkDisk "disk0" [] with MediaName = Some "APPLE SSD AP0512Z" }
let linked = Correlate.attachLinkSpeed usbDevices [ physical; synthesized; unrelated ]
let speedOf id = linked |> List.find (fun d -> d.DeviceId = id) |> fun d -> d.LinkSpeed
checkEq "physical disk gets its negotiated link speed" (Some "10 Gb/s") (speedOf "disk6")
checkEq "synthesized APFS disk does not double-report the connection" None (speedOf "disk7")
checkEq "unmatched disk is left alone" None (speedOf "disk0")
checkEq "no disks are added or dropped" 3 linked.Length
checkEq "an empty USB topology changes nothing"
    None
    (Correlate.attachLinkSpeed [] [ physical ] |> List.head |> fun d -> d.LinkSpeed)
// Media names are matched after trimming, since diskutil pads some values.
checkEq "media name is matched after trimming"
    (Some "10 Gb/s")
    (Correlate.attachLinkSpeed usbDevices [ { physical with MediaName = Some "  PSSD T7  " } ]
     |> List.head |> fun d -> d.LinkSpeed)
// A USB device with no reported speed must not blank out or match anything.
checkEq "a device reporting no speed contributes nothing"
    None
    (Correlate.attachLinkSpeed [ mkUsb "PSSD T7" None ] [ physical ] |> List.head |> fun d -> d.LinkSpeed)

printfn "Warnings.isSlowUsbSpeed — both macOS speed vocabularies"
check "modern 10 Gb/s is not slow" (not (Warnings.isSlowUsbSpeed "10 Gb/s"))
check "modern 5 Gb/s is not slow" (not (Warnings.isSlowUsbSpeed "5 Gb/s"))
check "USB 2.0 480 Mb/s is slow (new format)" (Warnings.isSlowUsbSpeed "480 Mb/s")
check "USB 2.0 is slow (legacy 'Up to' format)" (Warnings.isSlowUsbSpeed "Up to 480 Mb/s")
check "USB 1.1 full speed is slow" (Warnings.isSlowUsbSpeed "Up to 12 Mb/s")

// ===================================================================
printfn "Warnings.derive"
let critVol = mkVolume "d1" (Some "/full") (Some 96) false
let okVol = mkVolume "d2" (Some "/fine") (Some 40) false
let warnInv = mkInv [ mkDisk "d" [ critVol; okVol ] ]
let warns = Warnings.derive warnInv
check "critical capacity warning present"
    (warns |> List.exists (fun w -> w.Severity = Critical && w.Category = "Capacity" && w.Device = Some "d1"))
check "healthy volume not warned"
    (not (warns |> List.exists (fun w -> w.Device = Some "d2" && w.Category = "Capacity")))

// ===================================================================
printfn "Csv.render — formula injection neutralised"
let evilVol = { mkVolume "d1" (Some "/x") (Some 10) false with Name = Some "=HYPERLINK(1)" }
let csv = Render.Csv.render (mkInv [ mkDisk "d" [ evilVol ] ])
check "leading = is apostrophe-prefixed" (csv.Contains "'=HYPERLINK(1)")

// ===================================================================
printfn "Stats — verdict aggregates"
let sVols =
    [ mkVolume "v1" (Some "/a") (Some 99) false
      mkVolume "v2" (Some "/b") (Some 92) false
      mkVolume "v3" (Some "/c") (Some 10) false
      mkVolume "v4" None None true ]
checkEq "critical volumes counted" 1 (Stats.countVolumesAt Critical sVols)
checkEq "warning volumes counted" 1 (Stats.countVolumesAt Warning sVols)
checkEq "worst volume is the fullest" (Some "v1") (Stats.worstVolume sVols |> Option.map (fun v -> v.DeviceId))
checkEq "mounted count ignores unmounted" 3 (Stats.mountedCount sVols)

let sDisks =
    [ { mkDisk "disk0" [] with Internal = Some true; SizeBytes = Some 1000L }
      { mkDisk "disk1" [] with Internal = Some false; SizeBytes = Some 500L }
      { mkDisk "disk2" [] with Internal = Some false; SizeBytes = None; SmartStatus = Some "Failing" } ]
checkEq "internal/external split" (1, 2) (Stats.internalExternal sDisks)
checkEq "whole-disk bytes are additive" 1500L (Stats.totalDiskBytes sDisks)
checkEq "smart failures counted" 1 (Stats.smartFailing sDisks)
checkEq "Not Supported is not a failure" 0 (Stats.smartFailing [ { mkDisk "d" [] with SmartStatus = Some "Not Supported" } ])

printfn "Stats — pseudo-filesystems excluded from verdicts"
let mkFs fs mnt pct : FsUsage =
    { Filesystem = fs; MountPoint = mnt; SizeBytes = Some 100L
      UsedBytes = Some 99L; AvailBytes = Some 1L; CapacityPercent = Some pct }
let sFs =
    [ mkFs "devfs" "/dev" 100
      mkFs "com.apple.TimeMachine.2026-07-09.backup@/dev/disk7s1" "/Volumes/.timemachine/x/y.backup" 100
      mkFs "map auto_home" "/System/Volumes/Data/home" 100
      mkFs "/dev/disk3s5" "/System/Volumes/Data" 99 ]
check "devfs flagged pseudo" (Stats.isPseudoFs (mkFs "devfs" "/dev" 100))
check "timemachine snapshot flagged pseudo" (Stats.isPseudoFs (mkFs "com.apple.TimeMachine.x" "/Volumes/.timemachine/a" 100))
check "real volume not flagged pseudo" (not (Stats.isPseudoFs (mkFs "/dev/disk3s5" "/System/Volumes/Data" 99)))
checkEq "only real filesystems are actionable" 1 (Stats.actionableUsage sFs).Length
checkEq "worst ignores always-full pseudo fs"
    (Some "/System/Volumes/Data") (Stats.worstUsage sFs |> Option.map (fun f -> f.MountPoint))

// ===================================================================
printfn "Html — summary cards ordered by consequence"
// One healthy volume, no warnings, and every peripheral category empty.
let orderInv = mkInv [ mkDisk "d0" [ mkVolume "v0" (Some "/x") (Some 10) false ] ]
let orderHtml = Render.Html.render orderInv
// Card internals are spans (a <button> may only contain phrasing content).
let cardAt (label: string) = orderHtml.IndexOf(sprintf "<span class=\"lbl\">%s</span>" label)
check "every expected card is rendered"
    ([ "Critical"; "Advisories"; "Volumes"; "Physical Disks"; "NVMe"; "USB" ]
     |> List.forall (fun l -> cardAt l >= 0))
// The verdict leads even at zero: "no criticals" is itself the headline.
check "Critical leads even when the count is zero"
    (cardAt "Critical" < cardAt "Advisories")
check "Advisories precedes inventory" (cardAt "Advisories" < cardAt "Volumes")
check "Volumes precedes Physical Disks" (cardAt "Volumes" < cardAt "Physical Disks")
// Inventory categories with nothing to report sink below those with data.
check "empty inventory sinks below populated inventory"
    (cardAt "Physical Disks" < cardAt "NVMe")
check "empty USB sinks to the tail" (cardAt "USB" > cardAt "Physical Disks")

printfn "Html — card focus targets"
check "the ambiguous 'Warnings' card is renamed to Advisories"
    (orderHtml.Contains "<span class=\"lbl\">Advisories</span>")
check "no card is labelled Warnings (the panel keeps that name)"
    (not (orderHtml.Contains "<span class=\"lbl\">Warnings</span>"))
check "the Warnings panel still exists and is targetable"
    (orderHtml.Contains "id=\"panel-warnings\"")
check "the Volumes card focuses the Volumes panel"
    (orderHtml.Contains "data-target=\"panel-volumes\"")
// An empty category renders no panel, so its card must not become a control
// that does nothing when activated.
check "an empty category yields no dead control"
    (not (orderHtml.Contains "data-target=\"panel-usb-devices\""))
let focusTargets =
    Regex.Matches(orderHtml, "data-target=\"([^\"]+)\"")
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Seq.distinct
    |> Seq.toList
check "at least one focus target is rendered" (not focusTargets.IsEmpty)
check "every card target resolves to a real panel id"
    (focusTargets |> List.forall (fun t -> orderHtml.Contains(sprintf "id=\"%s\"" t)))
check "focusable cards are buttons, not divs"
    (orderHtml.Contains "<button type=\"button\" class=\"card card-live")

printfn "Html — severity cards go inert at zero"
// orderInv has no warnings at all, so neither severity card may be a control:
// focusing a panel that would show none of the card's own findings is a
// broken promise.
check "a zero-count Critical card is not a control"
    (not (orderHtml.Contains "data-filter=\"crit\""))
check "a zero-count Advisories card is not a control"
    (not (orderHtml.Contains "data-filter=\"warn\""))

// With real findings present, the severity cards become filtering controls.
let sevInv =
    let v = mkVolume "d1" (Some "/full") (Some 97) false      // -> Critical
    let base_ = mkInv [ mkDisk "d" [ v ] ]
    { base_ with Warnings = Warnings.derive base_ }
let sevHtml = Render.Html.render sevInv
check "a non-zero Critical card filters the panel to critical rows"
    (sevHtml.Contains "data-filter=\"crit\"")
check "the critical card carries the alert treatment when non-zero"
    (sevHtml.Contains "card-alert")
check "an active filter is announced so a short list is not read as missing data"
    (sevHtml.Contains "id=\"warnfilter\"")

// ===================================================================
printfn "Cli.stampName"
let stampTs = DateTime(2026, 7, 19, 18, 30, 45)
checkEq "stamp goes before the extension"
    "disk-report-20260719-183045.html" (Cli.stampName stampTs "disk-report.html")
checkEq "json output stamped"
    "inventory-20260719-183045.json" (Cli.stampName stampTs "inventory.json")
checkEq "extension is preserved"
    ".csv" (IO.Path.GetExtension(Cli.stampName stampTs "disks.csv"))
checkEq "a name without an extension still stamps cleanly"
    "report-20260719-183045" (Cli.stampName stampTs "report")
// All outputs of one run must share a single stamp so they sort together.
checkEq "every output of a run shares one stamp"
    1
    ([ "disk-report.html"; "inventory.json"; "disks.csv"; "report.md" ]
     |> List.map (fun n ->
         let s = Cli.stampName stampTs n
         s.Substring(s.IndexOf "-2026", 16))
     |> List.distinct
     |> List.length)

// ===================================================================
printfn "History.prune — retention per report type"
let tmpBase = IO.Path.Combine(IO.Path.GetTempPath(), "mde-prune-test-" + string (abs (hash stampTs)))
let tmpHistory = IO.Path.Combine(tmpBase, "history")
if IO.Directory.Exists tmpBase then IO.Directory.Delete(tmpBase, true)
IO.Directory.CreateDirectory tmpHistory |> ignore
// 5 instances of each of three report types.
for i in 1..5 do
    for name, ext in [ "disk-report", ".html"; "inventory", ".json"; "report", ".md" ] do
        IO.File.WriteAllText(
            IO.Path.Combine(tmpHistory, sprintf "%s-2026071%d-120000%s" name i ext), "x")
checkEq "15 archived files staged" 15 (IO.Directory.GetFiles tmpHistory).Length

let deleted = History.prune 3 tmpBase
checkEq "prunes down to the cap for every type" 6 deleted.Length
checkEq "cap applies per type, not globally" 9 (IO.Directory.GetFiles tmpHistory).Length
let survivingHtml =
    IO.Directory.GetFiles(tmpHistory, "disk-report-*.html")
    |> Array.map IO.Path.GetFileName |> Array.sort
checkEq "each type keeps exactly the cap" 3 survivingHtml.Length
check "the newest instances survive, oldest are dropped"
    (survivingHtml |> Array.forall (fun f -> f.Contains "20260713" || f.Contains "20260714" || f.Contains "20260715"))
checkEq "pruning is idempotent once at the cap" 0 (History.prune 3 tmpBase).Length
checkEq "a cap larger than the population deletes nothing" 0 (History.prune 99 tmpBase).Length
checkEq "a zero cap is treated as disabled, not delete-all" 0 (History.prune 0 tmpBase).Length
check "missing history directory is handled" ((History.prune 3 "/nonexistent-mde-path").IsEmpty)
IO.Directory.Delete(tmpBase, true)

// ===================================================================
printfn "Jout escaping"
checkEq "quote escaped"
    "{\"a\":\"x\\\"y\"}"
    (Jout.toString (Jout.JObj [ ("a", Jout.JStr "x\"y") ]))

// ===================================================================
printfn ""
printfn "══════════════════════════════════════"
printfn "  %d passed, %d failed" passed failed
printfn "══════════════════════════════════════"
if failed > 0 then exit 1
