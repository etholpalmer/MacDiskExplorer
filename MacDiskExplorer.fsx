(*
    MacDiskExplorer.fsx
    ===================================================================
    A single, self-contained F# script that inventories macOS storage.

    It collects data from native macOS command-line tools, parses the
    output (XML plist + JSON + plain text), correlates it into one
    strongly-typed immutable model, and emits multiple reports:

        - Console (ASCII tables)
        - HTML dashboard (self-contained, no CDNs)
        - JSON (full structured inventory)
        - CSV  (flattened disk/volume view)
        - Markdown (readable report)

    It also supports automatic warnings, history snapshots with diffing,
    and a --watch mode.

    Run:
        dotnet fsi MacDiskExplorer.fsx --all
        dotnet fsi MacDiskExplorer.fsx --html --json
        dotnet fsi MacDiskExplorer.fsx --console --watch

    Dependencies: none beyond the .NET BCL and macOS CLI tools.
    ===================================================================
*)

open System
open System.IO
open System.Text
open System.Text.Json
open System.Xml
open System.Xml.Linq
open System.Diagnostics
open System.Threading
open System.Text.RegularExpressions

// ===================================================================
// Module: Shell
// Runs external commands defensively. Never throws; returns None on
// failure so that a missing/failing tool degrades gracefully.
// ===================================================================
module Shell =

    /// Hard ceiling for any single command, in milliseconds.
    let private timeoutMs = 20000

    /// Run a command with an argument list. Returns Some stdout on
    /// success (or when stdout has content even with a non-zero exit),
    /// otherwise None.
    ///
    /// Both stdout and stderr are drained *concurrently* via async reads,
    /// which avoids the pipe-buffer deadlock that a read-stdout-then-read-
    /// stderr sequence risks, and the timeout is enforced with a real
    /// WaitForExit deadline (a synchronous ReadToEnd would block first,
    /// making the timeout meaningless and letting a hung child hang us).
    let run (cmd: string) (args: string list) : string option =
        try
            let psi = ProcessStartInfo(cmd)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            args |> List.iter psi.ArgumentList.Add
            use p = Process.Start(psi)
            let outTask = p.StandardOutput.ReadToEndAsync()
            let errTask = p.StandardError.ReadToEndAsync()
            let stdoutOf () =
                if outTask.IsCompletedSuccessfully then outTask.Result else ""
            if p.WaitForExit(timeoutMs) then
                // Process exited; the streams have hit EOF so the reads
                // finish promptly. The short join is a safety net only.
                Threading.Tasks.Task.WaitAll(
                    [| outTask :> Threading.Tasks.Task; errTask :> Threading.Tasks.Task |], 5000)
                |> ignore
                let out = stdoutOf ()
                if p.ExitCode = 0 || out.Length > 0 then Some out else None
            else
                // Timed out — kill the whole process tree and salvage
                // whatever stdout we managed to read.
                (try p.Kill(true) with _ -> ())
                let out = stdoutOf ()
                if out.Length > 0 then Some out else None
        with _ -> None

    /// Run and return a trimmed single-line result, if any.
    let runLine (cmd: string) (args: string list) : string option =
        run cmd args
        |> Option.map (fun s -> s.Trim())
        |> Option.filter (fun s -> s.Length > 0)

// ===================================================================
// Module: Plist
// A small, defensive Apple XML-plist parser built on XLinq.
// ===================================================================
module Plist =

    type PVal =
        | PDict of Map<string, PVal>
        | PArray of PVal list
        | PString of string
        | PInt of int64
        | PReal of float
        | PBool of bool
        | PData of string
        | PDate of string
        | PNone

    let rec private parseNode (el: XElement) : PVal =
        match el.Name.LocalName with
        | "dict" ->
            let children = el.Elements() |> Seq.toList
            let rec pairs acc (lst: XElement list) =
                match lst with
                | (k: XElement) :: v :: rest when k.Name.LocalName = "key" ->
                    pairs ((k.Value, parseNode v) :: acc) rest
                | _ -> List.rev acc
            PDict(pairs [] children |> Map.ofList)
        | "array" -> PArray(el.Elements() |> Seq.map parseNode |> Seq.toList)
        | "string" -> PString el.Value
        | "integer" ->
            match Int64.TryParse el.Value with
            | true, v -> PInt v
            | _ -> PString el.Value
        | "real" ->
            match Double.TryParse(el.Value, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> PReal v
            | _ -> PString el.Value
        | "true" -> PBool true
        | "false" -> PBool false
        | "data" -> PData(el.Value.Trim())
        | "date" -> PDate el.Value
        | _ -> PNone

    /// Parse a plist XML string into a PVal. DTD processing is ignored
    /// so Apple's external DOCTYPE never triggers a network fetch.
    let parse (xml: string) : PVal option =
        if String.IsNullOrWhiteSpace xml then None
        else
            try
                let settings = XmlReaderSettings()
                settings.DtdProcessing <- DtdProcessing.Ignore
                settings.XmlResolver <- null
                use sr = new StringReader(xml)
                use reader = XmlReader.Create(sr, settings)
                let doc = XDocument.Load(reader)
                doc.Root.Elements() |> Seq.tryHead |> Option.map parseNode
            with _ -> None

    // ---- Accessors (all total, never throw) ----
    let key (k: string) (v: PVal) : PVal option =
        match v with
        | PDict d -> Map.tryFind k d
        | _ -> None

    let asString (v: PVal) : string option =
        match v with
        | PString s -> Some s
        | PInt i -> Some(string i)
        | PReal r -> Some(string r)
        | PBool b -> Some(if b then "Yes" else "No")
        | _ -> None

    let asInt64 (v: PVal) : int64 option =
        match v with
        | PInt i -> Some i
        | PReal r -> Some(int64 r)
        | PString s ->
            match Int64.TryParse s with
            | true, x -> Some x
            | _ -> None
        | _ -> None

    let asBool (v: PVal) : bool option =
        match v with
        | PBool b -> Some b
        | PString s -> Some(s = "Yes" || s = "true" || s = "True" || s = "1")
        | _ -> None

    let asArray (v: PVal) : PVal list =
        match v with
        | PArray a -> a
        | _ -> []

    // Convenience: get string/int/bool at a key from a dict PVal.
    let strAt k v = key k v |> Option.bind asString
    let intAt k v = key k v |> Option.bind asInt64
    let boolAt k v = key k v |> Option.bind asBool
    let arrAt k v = key k v |> Option.map asArray |> Option.defaultValue []

// ===================================================================
// Module: Jsonp
// Thin helpers over System.Text.Json for reading system_profiler JSON.
// ===================================================================
module Jsonp =

    let parse (json: string) : JsonElement option =
        if String.IsNullOrWhiteSpace json then None
        else
            try
                // Clone() detaches the element from the document so the
                // document's pooled buffers can be returned on dispose.
                use doc = JsonDocument.Parse(json)
                Some(doc.RootElement.Clone())
            with _ -> None

    let prop (name: string) (el: JsonElement) : JsonElement option =
        if el.ValueKind = JsonValueKind.Object then
            match el.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        else None

    let asStr (el: JsonElement) : string option =
        match el.ValueKind with
        | JsonValueKind.String -> Some(el.GetString())
        | JsonValueKind.Number -> Some(el.GetRawText())
        | JsonValueKind.True -> Some "true"
        | JsonValueKind.False -> Some "false"
        | _ -> None

    let strProp (name: string) (el: JsonElement) : string option =
        prop name el |> Option.bind asStr

    let items (el: JsonElement) : JsonElement list =
        if el.ValueKind = JsonValueKind.Array then
            el.EnumerateArray() |> Seq.toList
        else []

// ===================================================================
// Module: Model
// The unified, immutable inventory model.
// ===================================================================
module Model =

    type Severity =
        | Info
        | Warning
        | Critical

    type Volume =
        { DeviceId: string
          Name: string option
          MountPoint: string option
          FileSystem: string option
          SizeBytes: int64 option
          UsedBytes: int64 option
          AvailBytes: int64 option
          CapacityPercent: int option
          ReadOnly: bool }

    type PhysicalDisk =
        { DeviceId: string
          MediaName: string option
          SizeBytes: int64 option
          Internal: bool option
          SolidState: bool option
          BusProtocol: string option
          Removable: bool option
          SmartStatus: string option
          /// Negotiated link speed of the connection, e.g. "10 Gb/s".
          /// Correlated from the USB/Thunderbolt topology; None when the bus
          /// does not report one.
          LinkSpeed: string option
          /// True for synthesized APFS container disks, which have no
          /// connection of their own; they share their physical store's.
          Virtual: bool option
          Volumes: Volume list }

    type UsbDevice =
        { Name: string
          Manufacturer: string option
          Speed: string option
          SerialNumber: string option
          Vendor: string option }

    type ThunderboltDevice =
        { Name: string
          Vendor: string option
          Speed: string option }

    type NvmeDevice =
        { Name: string
          Model: string option
          Capacity: string option
          TrimSupport: bool option
          SmartStatus: string option }

    type NetworkMount =
        { Source: string
          MountPoint: string
          FsType: string }

    type FsUsage =
        { Filesystem: string
          MountPoint: string
          SizeBytes: int64 option
          UsedBytes: int64 option
          AvailBytes: int64 option
          CapacityPercent: int option }

    type Warning =
        { Severity: Severity
          Category: string
          Message: string
          Device: string option }

    type Computer =
        { HostName: string
          Model: string option
          OsVersion: string option }

    type Inventory =
        { Timestamp: DateTime
          Computer: Computer
          Disks: PhysicalDisk list
          Usb: UsbDevice list
          Thunderbolt: ThunderboltDevice list
          Nvme: NvmeDevice list
          NetworkMounts: NetworkMount list
          FsUsage: FsUsage list
          Warnings: Warning list }

    /// All volumes flattened across every disk.
    let allVolumes (inv: Inventory) : Volume list =
        inv.Disks |> List.collect (fun d -> d.Volumes)

// ===================================================================
// Module: Fmt
// Formatting helpers shared across all renderers.
// ===================================================================
module Fmt =
    open Model

    let private units = [| "B"; "KB"; "MB"; "GB"; "TB"; "PB" |]

    /// Human-readable byte size, e.g. 1.50 TB.
    let bytes (b: int64 option) : string =
        match b with
        | None -> "-"
        | Some n when n < 0L -> "-"
        | Some 0L -> "0 B"
        | Some n ->
            let mutable v = float n
            let mutable i = 0
            while v >= 1024.0 && i < units.Length - 1 do
                v <- v / 1024.0
                i <- i + 1
            if i = 0 then sprintf "%d %s" n units.[i]
            else sprintf "%.2f %s" v units.[i]

    let str (s: string option) = s |> Option.defaultValue "-"

    let boolYN (b: bool option) =
        match b with
        | Some true -> "Yes"
        | Some false -> "No"
        | None -> "-"

    let pct (p: int option) =
        match p with
        | Some v -> sprintf "%d%%" v
        | None -> "-"

    /// Health traffic-light icon based on used-capacity percentage.
    let healthIcon (p: int option) =
        match p with
        | Some v when v >= 95 -> "🔴"
        | Some v when v >= 90 -> "🟡"
        | _ -> "🟢"

    let severityIcon =
        function
        | Critical -> "🔴"
        | Warning -> "🟡"
        | Info -> "🔵"

    let severityText =
        function
        | Critical -> "CRITICAL"
        | Warning -> "WARNING"
        | Info -> "INFO"

    /// Map a bus protocol to a friendly kind + icon.
    let classifyBus (bus: string option) (isInternal: bool option) : string * string =
        match bus with
        | Some b ->
            let bl = b.ToLowerInvariant()
            if bl.Contains "usb" then "USB", "🔌"
            elif bl.Contains "thunderbolt" then "Thunderbolt", "⚡"
            elif bl.Contains "pci" then "PCIe", "💽"
            elif bl.Contains "sata" then "SATA", "💽"
            elif bl.Contains "sas" then "SAS", "💽"
            elif bl.Contains "fibre" then "Fibre", "💽"
            elif bl.Contains "firewire" then "FireWire", "🔌"
            elif bl.Contains "disk image" then "Disk Image", "📀"
            elif bl.Contains "apple fabric" then "Apple Fabric", "💽"
            else b, "💽"
        | None -> (if isInternal = Some true then "Internal", "💽" else "Unknown", "❓")

    /// A safe timestamp string for filenames.
    let stamp (t: DateTime) = t.ToString("yyyyMMdd-HHmmss")
    let iso (t: DateTime) = t.ToString("yyyy-MM-ddTHH:mm:ss")

// ===================================================================
// Module: Collectors
// Each collector runs a command, parses it, and returns partial data.
// ===================================================================
module Collectors =
    open Model
    open Plist

    // ---- Computer info ----
    let collectComputer () : Computer =
        { HostName =
            Shell.runLine "scutil" [ "--get"; "ComputerName" ]
            |> Option.defaultValue Environment.MachineName
          Model = Shell.runLine "sysctl" [ "-n"; "hw.model" ]
          OsVersion =
            Shell.runLine "sw_vers" [ "-productVersion" ]
            |> Option.map (fun v ->
                match Shell.runLine "sw_vers" [ "-buildVersion" ] with
                | Some b -> sprintf "%s (%s)" v b
                | None -> v) }

    // ---- diskutil info -plist <id> ----
    let private diskInfo (deviceId: string) : PVal option =
        Shell.run "diskutil" [ "info"; "-plist"; deviceId ]
        |> Option.bind Plist.parse

    /// Build a Volume record from a diskutil info plist.
    /// Live usage (used/avail/percent) is intentionally left empty here:
    /// for APFS volumes diskutil reports container-wide free space, which
    /// yields a misleading ~100%. df fills authoritative usage for mounted
    /// volumes in the correlation step; unmounted volumes correctly report
    /// no capacity rather than a false 100%.
    let private volumeFromInfo (deviceId: string) (info: PVal) : Volume =
        let size = intAt "TotalSize" info |> Option.orElse (intAt "Size" info)
        let writable = boolAt "WritableVolume" info
        { DeviceId = deviceId
          Name =
            strAt "VolumeName" info
            |> Option.filter (fun n -> n.Trim().Length > 0)
          MountPoint =
            strAt "MountPoint" info
            |> Option.filter (fun m -> m.Trim().Length > 0)
          FileSystem =
            strAt "FilesystemName" info
            |> Option.orElse (strAt "FilesystemType" info)
          SizeBytes = size
          UsedBytes = None
          AvailBytes = None
          CapacityPercent = None
          ReadOnly = (writable = Some false) }

    /// Collect physical disks and their volumes via diskutil.
    let collectDisks () : PhysicalDisk list =
        match Shell.run "diskutil" [ "list"; "-plist" ] |> Option.bind Plist.parse with
        | None -> []
        | Some root ->
            let wholes = arrAt "AllDisksAndPartitions" root
            wholes
            |> List.choose (fun w ->
                match strAt "DeviceIdentifier" w with
                | None -> None
                | Some wholeId ->
                    // Enrich the whole disk from `diskutil info`.
                    let info = diskInfo wholeId
                    let getStr k = info |> Option.bind (strAt k)
                    let getInt k = info |> Option.bind (intAt k)
                    let getBool k = info |> Option.bind (boolAt k)

                    // Child volumes: standard partitions and APFS volumes.
                    let partIds =
                        (arrAt "Partitions" w @ arrAt "APFSVolumes" w)
                        |> List.choose (strAt "DeviceIdentifier")
                        |> List.distinct

                    let volumes =
                        partIds
                        |> List.choose (fun pid ->
                            diskInfo pid |> Option.map (volumeFromInfo pid))
                        |> List.filter (fun v ->
                            // Keep volumes that are mounted or named; drop empty slices.
                            v.MountPoint.IsSome || v.Name.IsSome || v.SizeBytes.IsSome)

                    Some
                        { DeviceId = wholeId
                          MediaName = getStr "MediaName" |> Option.orElse (getStr "IORegistryEntryName")
                          SizeBytes = getInt "TotalSize" |> Option.orElse (intAt "Size" w)
                          Internal = getBool "Internal"
                          SolidState = getBool "SolidState"
                          BusProtocol = getStr "BusProtocol"
                          Removable =
                            getBool "RemovableMedia"
                            |> Option.orElse (getBool "Removable")
                            |> Option.orElse (getBool "RemovableMediaOrExternalDevice")
                          SmartStatus = getStr "SMARTStatus"
                          LinkSpeed = None   // filled by Correlate.attachLinkSpeed
                          Virtual = getStr "VirtualOrPhysical" |> Option.map (fun v -> v = "Virtual")
                          Volumes = volumes })

    // ---- system_profiler (JSON) generic recursive flattener ----
    let private spItems (dataType: string) : JsonElement list =
        match Shell.run "system_profiler" [ dataType; "-json" ] |> Option.bind Jsonp.parse with
        | None -> []
        | Some root ->
            match Jsonp.prop dataType root with
            | Some arr -> Jsonp.items arr
            | None -> []

    /// Recursively walk a system_profiler node and its `_items` children.
    let rec private walk (el: JsonElement) : JsonElement list =
        let children =
            match Jsonp.prop "_items" el with
            | Some arr -> Jsonp.items arr |> List.collect walk
            | None -> []
        el :: children

    /// First property name that yields a value, so one extractor serves
    /// several macOS key vocabularies.
    let private strAny (names: string list) (el: JsonElement) =
        names |> List.tryPick (fun n -> Jsonp.strProp n el)

    /// First data type that returns anything.
    let rec private firstPopulated (dataTypes: string list) : JsonElement list =
        match dataTypes with
        | [] -> []
        | t :: rest ->
            match spItems t with
            | [] -> firstPopulated rest
            | items -> items

    /// macOS 26 replaced SPUSBDataType with SPUSBHostDataType and renamed
    /// every property (`speed` -> `USBDeviceKeyLinkSpeed`, and so on). Query
    /// the current type first and fall back to the legacy one, accepting
    /// either vocabulary, so the collector works across macOS versions.
    let collectUsb () : UsbDevice list =
        firstPopulated [ "SPUSBHostDataType"; "SPUSBDataType" ]
        |> List.collect walk
        |> List.choose (fun el ->
            match Jsonp.strProp "_name" el with
            | Some name when name <> "USB" ->
                Some
                    { Name = name
                      Manufacturer = strAny [ "USBDeviceKeyVendorName"; "manufacturer" ] el
                      Speed = strAny [ "USBDeviceKeyLinkSpeed"; "speed" ] el
                      SerialNumber = strAny [ "USBDeviceKeySerialNumber"; "serial_num" ] el
                      Vendor = strAny [ "USBDeviceKeyVendorID"; "vendor_id" ] el }
            | _ -> None)
        // Bus/hub nodes carry none of these; only real devices do.
        |> List.filter (fun d -> d.Speed.IsSome || d.SerialNumber.IsSome || d.Manufacturer.IsSome)

    let collectThunderbolt () : ThunderboltDevice list =
        spItems "SPThunderboltDataType"
        |> List.collect walk
        |> List.choose (fun el ->
            match Jsonp.strProp "_name" el with
            | Some name ->
                Some
                    { Name = name
                      Vendor = Jsonp.strProp "vendor_name_key" el |> Option.orElse (Jsonp.strProp "device_name_key" el)
                      Speed = Jsonp.strProp "current_speed_key" el |> Option.orElse (Jsonp.strProp "link_status_key" el) }
            | None -> None)

    let collectNvme () : NvmeDevice list =
        spItems "SPNVMeDataType"
        |> List.collect walk
        |> List.choose (fun el ->
            match Jsonp.strProp "_name" el with
            | Some name ->
                let cap = Jsonp.strProp "size" el
                match cap, Jsonp.strProp "device_model" el with
                | None, None -> None
                | _ ->
                    Some
                        { Name = name
                          Model = Jsonp.strProp "device_model" el
                          Capacity = cap
                          TrimSupport =
                            Jsonp.strProp "spnvme_trim_support" el
                            |> Option.map (fun s -> s.ToLowerInvariant().Contains "yes")
                          SmartStatus = Jsonp.strProp "spnvme_smart_status" el }
            | None -> None)

    // ---- df -k (block usage in 1024-byte units) ----

    /// Pure parser for `df -k` output. Kept separate from IO so it can
    /// be unit-tested against captured fixtures.
    let parseDf (out: string) : FsUsage list =
        let lines = out.Split('\n')
        if lines.Length <= 1 then []
        else
            lines.[1..]
            |> Array.choose (fun line ->
                let line = line.TrimEnd()
                if line.Length = 0 then None
                else
                    // Filesystem 1024-blocks Used Available Capacity iused ifree %iused MountedOn
                    let parts =
                        line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length < 9 then None
                    else
                        let toBytes (s: string) =
                            match Int64.TryParse s with
                            | true, v -> Some(v * 1024L)
                            | _ -> None
                        let capPct =
                            let c = parts.[4].TrimEnd('%')
                            match Int32.TryParse c with
                            | true, v -> Some v
                            | _ -> None
                        // Mount point is everything from token 8 onward (may contain spaces).
                        let mount = String.Join(" ", parts.[8..])
                        Some
                            { Filesystem = parts.[0]
                              MountPoint = mount
                              SizeBytes = toBytes parts.[1]
                              UsedBytes = toBytes parts.[2]
                              AvailBytes = toBytes parts.[3]
                              CapacityPercent = capPct })
            |> Array.toList

    let collectFsUsage () : FsUsage list =
        Shell.run "df" [ "-k" ] |> Option.map parseDf |> Option.defaultValue []

    // ---- mount (network mount detection) ----
    let private mountRegex = Regex(@"^(.*?) on (.*?) \((.*)\)$", RegexOptions.Compiled)
    let private networkFs = set [ "nfs"; "smbfs"; "afpfs"; "webdav"; "ftp"; "cifs" ]

    /// Pure parser for `mount` output, keeping only network mounts.
    let parseMounts (out: string) : NetworkMount list =
        out.Split('\n')
        |> Array.choose (fun line ->
            let m = mountRegex.Match(line.Trim())
            if not m.Success then None
            else
                let src = m.Groups.[1].Value
                let mnt = m.Groups.[2].Value
                let opts = m.Groups.[3].Value.Split(',') |> Array.map (fun s -> s.Trim())
                let fsType = if opts.Length > 0 then opts.[0] else ""
                let isNetwork =
                    networkFs.Contains(fsType.ToLowerInvariant())
                    || src.Contains "://"
                    || (src.StartsWith "//")
                    || (src.Contains ":" && src.Contains "/")
                if isNetwork then
                    Some { Source = src; MountPoint = mnt; FsType = fsType }
                else None)
        |> Array.toList

    let collectNetworkMounts () : NetworkMount list =
        Shell.run "mount" [] |> Option.map parseMounts |> Option.defaultValue []

// ===================================================================
// Module: Correlate
// Best-effort cross-source enrichment. Where correlation isn't
// possible the data simply stays in its own table.
// ===================================================================
module Correlate =
    open Model

    /// Attach the negotiated link speed to each disk by matching diskutil's
    /// MediaName against the USB topology's device name ("PSSD T7").
    ///
    /// Synthesized APFS container disks are skipped: they report the same
    /// MediaName as the physical store they sit on, so matching them too
    /// would report one connection twice.
    let attachLinkSpeed (usb: UsbDevice list) (disks: PhysicalDisk list) : PhysicalDisk list =
        let speedByName =
            usb
            |> List.choose (fun u ->
                match u.Speed with
                | Some s when u.Name.Trim().Length > 0 -> Some(u.Name.Trim(), s)
                | _ -> None)
            |> List.distinctBy fst
            |> Map.ofList

        if speedByName.IsEmpty then disks
        else
            disks
            |> List.map (fun d ->
                if d.Virtual = Some true then d
                else
                    match d.MediaName |> Option.bind (fun m -> Map.tryFind (m.Trim()) speedByName) with
                    | Some speed -> { d with LinkSpeed = Some speed }
                    | None -> d)

    /// Fill in volume usage (used/avail/percent) from df where diskutil
    /// did not provide it, matched by mount point.
    let enrichVolumesWithDf (usage: FsUsage list) (disks: PhysicalDisk list) : PhysicalDisk list =
        let byMount =
            usage
            |> List.choose (fun u -> if u.MountPoint.Length > 0 then Some(u.MountPoint, u) else None)
            |> Map.ofList

        disks
        |> List.map (fun d ->
            let vols =
                d.Volumes
                |> List.map (fun v ->
                    match v.MountPoint |> Option.bind (fun m -> Map.tryFind m byMount) with
                    | Some u ->
                        // df is authoritative for a mounted volume's live usage.
                        { v with
                            SizeBytes = u.SizeBytes |> Option.orElse v.SizeBytes
                            UsedBytes = u.UsedBytes
                            AvailBytes = u.AvailBytes
                            CapacityPercent = u.CapacityPercent }
                    | None -> v)
            { d with Volumes = vols })

// ===================================================================
// Module: Warnings
// Derives warnings automatically from the collected data.
// ===================================================================
module Warnings =
    open Model

    let private crit cat dev msg =
        { Severity = Critical; Category = cat; Message = msg; Device = dev }
    let private warn cat dev msg =
        { Severity = Warning; Category = cat; Message = msg; Device = dev }
    let private info cat dev msg =
        { Severity = Info; Category = cat; Message = msg; Device = dev }

    /// USB 2.0 (or slower) speed detection from a speed string.
    /// Public so the HTML report can label the same devices consistently.
    let isSlowUsbSpeed (speed: string) =
        let s = speed.ToLowerInvariant()
        s.Contains "480 mb" || s.Contains "12 mb" || s.Contains "1.5 mb" || s.Contains "full speed" || s.Contains "low speed"

    let derive (inv: Inventory) : Warning list =
        [
            // Capacity thresholds on mounted volumes.
            for d in inv.Disks do
                for v in d.Volumes do
                    match v.CapacityPercent with
                    | Some p when p >= 95 ->
                        yield crit "Capacity" (Some v.DeviceId)
                                  (sprintf "%s is %d%% full" (v.Name |> Option.defaultValue v.DeviceId) p)
                    | Some p when p >= 90 ->
                        yield warn "Capacity" (Some v.DeviceId)
                                  (sprintf "%s is %d%% full" (v.Name |> Option.defaultValue v.DeviceId) p)
                    | _ -> ()

            // Read-only mounted volumes (excluding the sealed system root).
            for d in inv.Disks do
                for v in d.Volumes do
                    if v.ReadOnly && v.MountPoint.IsSome && v.MountPoint <> Some "/" then
                        yield info "Read-only" (Some v.DeviceId)
                                  (sprintf "%s is mounted read-only at %s"
                                        (v.Name |> Option.defaultValue v.DeviceId)
                                        (v.MountPoint |> Option.defaultValue "?"))

            // SMART failures.
            for d in inv.Disks do
                match d.SmartStatus with
                | Some s when s.Length > 0 && s <> "Verified" && s <> "Not Supported" ->
                    yield crit "SMART" (Some d.DeviceId) (sprintf "%s SMART status: %s" d.DeviceId s)
                | _ -> ()

            // TRIM disabled on NVMe SSDs.
            for n in inv.Nvme do
                match n.TrimSupport with
                | Some false ->
                    yield warn "TRIM" (Some n.Name) (sprintf "TRIM appears disabled on %s" n.Name)
                | _ -> ()

            // Slow USB devices.
            for u in inv.Usb do
                match u.Speed with
                | Some sp when isSlowUsbSpeed sp ->
                    yield warn "USB Speed" (Some u.Name) (sprintf "%s is a slow USB device (%s)" u.Name sp)
                | _ -> ()

            // Missing external/backup disk.
            let hasExternal =
                inv.Disks |> List.exists (fun d -> d.Internal = Some false)
            if not hasExternal then
                yield info "Backup" None "No external disk detected — no local backup target present"

            // Network mounts present (surface for connectivity awareness).
            for nm in inv.NetworkMounts do
                yield info "Network" (Some nm.MountPoint)
                          (sprintf "Network mount %s (%s) — verify connectivity" nm.Source nm.FsType)
        ]

// ===================================================================
// Module: Engine
// Ties collectors + correlation + warnings into one inventory.
// ===================================================================
module Engine =
    open Model

    let collect () : Inventory =
        let computer = Collectors.collectComputer ()
        let fsUsage = Collectors.collectFsUsage ()
        let usb = Collectors.collectUsb ()
        let disks =
            Collectors.collectDisks ()
            |> Correlate.enrichVolumesWithDf fsUsage
            |> Correlate.attachLinkSpeed usb
        let baseInv =
            { Timestamp = DateTime.Now
              Computer = computer
              Disks = disks
              Usb = usb
              Thunderbolt = Collectors.collectThunderbolt ()
              Nvme = Collectors.collectNvme ()
              NetworkMounts = Collectors.collectNetworkMounts ()
              FsUsage = fsUsage
              Warnings = [] }
        { baseInv with Warnings = Warnings.derive baseInv }

// ===================================================================
// Module: Jout
// Minimal JSON writer for our own structured export (option-aware).
// ===================================================================
module Jout =

    type JVal =
        | JStr of string
        | JNum of string
        | JBool of bool
        | JNull
        | JArr of JVal list
        | JObj of (string * JVal) list

    let private escape (s: string) =
        let sb = StringBuilder(s.Length + 8)
        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore
        sb.ToString()

    let rec private write (sb: StringBuilder) (v: JVal) =
        match v with
        | JNull -> sb.Append "null" |> ignore
        | JBool b -> sb.Append(if b then "true" else "false") |> ignore
        | JNum n -> sb.Append n |> ignore
        | JStr s -> sb.Append('"').Append(escape s).Append('"') |> ignore
        | JArr xs ->
            sb.Append '[' |> ignore
            xs |> List.iteri (fun i x ->
                if i > 0 then sb.Append ',' |> ignore
                write sb x)
            sb.Append ']' |> ignore
        | JObj ps ->
            sb.Append '{' |> ignore
            ps |> List.iteri (fun i (k, x) ->
                if i > 0 then sb.Append ',' |> ignore
                sb.Append('"').Append(escape k).Append("\":") |> ignore
                write sb x)
            sb.Append '}' |> ignore

    let toString (v: JVal) : string =
        let sb = StringBuilder()
        write sb v
        sb.ToString()

    // Option helpers.
    let jStrO = function Some s -> JStr s | None -> JNull
    let jIntO = function Some (i: int64) -> JNum(string i) | None -> JNull
    let jIntO32 = function Some (i: int) -> JNum(string i) | None -> JNull
    let jBoolO = function Some b -> JBool b | None -> JNull

// ===================================================================
// Module: Stats
// Aggregates for the report headers.
//
// Kept pure and in one place so the "honest numbers" rule is enforced
// centrally: a total is only offered where the underlying figures are
// actually additive. Whole-disk capacity is (distinct physical devices);
// volume and df rows are NOT, because every APFS volume in a container
// reports that container's size, so summing them multiplies one disk by
// its volume count. Those surfaces get severity and worst-offender
// figures instead of a fabricated total.
// ===================================================================
module Stats =
    open Model

    /// Severity implied by a volume's fill level (mirrors Warnings.derive).
    let volumeSeverity (v: Volume) : Severity option =
        match v.CapacityPercent with
        | Some p when p >= 95 -> Some Critical
        | Some p when p >= 90 -> Some Warning
        | _ -> None

    let countSeverity (sev: Severity) (ws: Warning list) =
        ws |> List.filter (fun w -> w.Severity = sev) |> List.length

    let countVolumesAt (sev: Severity) (vols: Volume list) =
        vols |> List.filter (fun v -> volumeSeverity v = Some sev) |> List.length

    /// The fullest volume, among those reporting a usable figure.
    let worstVolume (vols: Volume list) : Volume option =
        vols
        |> List.filter (fun v -> v.CapacityPercent.IsSome)
        |> List.sortByDescending (fun v -> v.CapacityPercent)
        |> List.tryHead

    /// Pseudo-filesystems and snapshot mounts. devfs is 100% full by
    /// definition, and Time Machine snapshot mounts report the capacity of
    /// the snapshot rather than anything the operator can act on. They stay
    /// visible in the table (the inventory is complete) but must never be
    /// counted toward a verdict, or the report cries wolf.
    let isPseudoFs (f: FsUsage) =
        let fs = f.Filesystem.ToLowerInvariant()
        let mnt = f.MountPoint.ToLowerInvariant()
        fs = "devfs"
        || fs = "map"
        || fs.StartsWith "map "
        || fs.StartsWith "com.apple.timemachine"
        || mnt = "/dev"
        || mnt.StartsWith "/volumes/.timemachine"

    /// Rows a verdict may legitimately be drawn from.
    let actionableUsage (rows: FsUsage list) = rows |> List.filter (isPseudoFs >> not)

    let worstUsage (rows: FsUsage list) : FsUsage option =
        rows
        |> actionableUsage
        |> List.filter (fun r -> r.CapacityPercent.IsSome)
        |> List.sortByDescending (fun r -> r.CapacityPercent)
        |> List.tryHead

    /// Additive: whole disks are distinct physical devices.
    let totalDiskBytes (disks: PhysicalDisk list) =
        disks |> List.sumBy (fun d -> d.SizeBytes |> Option.defaultValue 0L)

    let internalExternal (disks: PhysicalDisk list) =
        let internals = disks |> List.filter (fun d -> d.Internal = Some true) |> List.length
        internals, disks.Length - internals

    let smartFailing (disks: PhysicalDisk list) =
        disks
        |> List.filter (fun d ->
            match d.SmartStatus with
            | Some s -> s.Length > 0 && s <> "Verified" && s <> "Not Supported"
            | None -> false)
        |> List.length

    let mountedCount (vols: Volume list) =
        vols |> List.filter (fun v -> v.MountPoint.IsSome) |> List.length

    /// Human label for a volume: its name when it has one, else its device id.
    let volumeLabel (v: Volume) = v.Name |> Option.defaultValue v.DeviceId

// ===================================================================
// Module: Render
// All output formats. Each takes the Inventory and returns a string
// (or writes a file, for console).
// ===================================================================
module Render =
    open Model
    open Jout

    // ---------------------------------------------------------------
    // Console: simple aligned ASCII tables.
    // ---------------------------------------------------------------
    module Console =

        let private table (headers: string list) (rows: string list list) : string =
            let cols = headers.Length
            let widths = Array.create cols 0
            let consider (cells: string list) =
                cells |> List.iteri (fun i c ->
                    if i < cols then widths.[i] <- max widths.[i] c.Length)
            consider headers
            rows |> List.iter consider
            let pad (i: int) (s: string) = s.PadRight(widths.[i])
            let sb = StringBuilder()
            let line (cells: string list) =
                cells |> List.iteri (fun i c ->
                    if i > 0 then sb.Append "  " |> ignore
                    sb.Append(pad i c) |> ignore)
                sb.Append '\n' |> ignore
            line headers
            let sep = widths |> Array.map (fun w -> String('-', w)) |> Array.toList
            line sep
            rows |> List.iter line
            sb.ToString()

        let render (inv: Inventory) : string =
            let sb = StringBuilder()
            let heading t = sb.Append("\n== ").Append(t: string).Append(" ==\n") |> ignore

            sb.Append("╔══════════════════════════════════════════════════╗\n") |> ignore
            sb.Append("║           Mac Disk Explorer — Report              ║\n") |> ignore
            sb.Append("╚══════════════════════════════════════════════════╝\n") |> ignore
            sb.Append(sprintf "Host:      %s\n" inv.Computer.HostName) |> ignore
            sb.Append(sprintf "Model:     %s\n" (Fmt.str inv.Computer.Model)) |> ignore
            sb.Append(sprintf "macOS:     %s\n" (Fmt.str inv.Computer.OsVersion)) |> ignore
            sb.Append(sprintf "Generated: %s\n" (Fmt.iso inv.Timestamp)) |> ignore

            // Summary
            heading "Summary"
            sb.Append(sprintf "Physical disks : %d\n" inv.Disks.Length) |> ignore
            sb.Append(sprintf "Volumes        : %d\n" (allVolumes inv).Length) |> ignore
            sb.Append(sprintf "USB devices    : %d\n" inv.Usb.Length) |> ignore
            sb.Append(sprintf "Thunderbolt    : %d\n" inv.Thunderbolt.Length) |> ignore
            sb.Append(sprintf "NVMe devices   : %d\n" inv.Nvme.Length) |> ignore
            sb.Append(sprintf "Network mounts : %d\n" inv.NetworkMounts.Length) |> ignore
            sb.Append(sprintf "Warnings       : %d\n" inv.Warnings.Length) |> ignore

            // Physical disks
            heading "Physical Disks"
            let diskRows =
                inv.Disks
                |> List.map (fun d ->
                    let kind, icon = Fmt.classifyBus d.BusProtocol d.Internal
                    [ d.DeviceId
                      Fmt.str d.MediaName
                      Fmt.bytes d.SizeBytes
                      sprintf "%s %s" icon kind
                      Fmt.str d.LinkSpeed
                      (if d.Internal = Some true then "Internal" else "External")
                      (if d.SolidState = Some true then "SSD" else "HDD")
                      Fmt.str d.SmartStatus ])
            sb.Append(table [ "Device"; "Media"; "Size"; "Bus"; "Link"; "Location"; "Type"; "SMART" ] diskRows)
            |> ignore

            // Volumes
            heading "Volumes"
            let volRows =
                allVolumes inv
                |> List.map (fun v ->
                    [ Fmt.healthIcon v.CapacityPercent
                      v.DeviceId
                      Fmt.str v.Name
                      Fmt.str v.MountPoint
                      Fmt.str v.FileSystem
                      Fmt.bytes v.SizeBytes
                      Fmt.bytes v.UsedBytes
                      Fmt.pct v.CapacityPercent
                      (if v.ReadOnly then "RO" else "RW") ])
            sb.Append(table [ ""; "Device"; "Name"; "Mount"; "FS"; "Size"; "Used"; "Full"; "Mode" ] volRows) |> ignore

            // USB
            if not inv.Usb.IsEmpty then
                heading "USB Devices 🔌"
                let rows =
                    inv.Usb |> List.map (fun u ->
                        [ u.Name; Fmt.str u.Manufacturer; Fmt.str u.Speed ])
                sb.Append(table [ "Name"; "Manufacturer"; "Speed" ] rows) |> ignore

            // Thunderbolt
            if not inv.Thunderbolt.IsEmpty then
                heading "Thunderbolt Devices ⚡"
                let rows =
                    inv.Thunderbolt |> List.map (fun t ->
                        [ t.Name; Fmt.str t.Vendor; Fmt.str t.Speed ])
                sb.Append(table [ "Name"; "Vendor"; "Speed" ] rows) |> ignore

            // NVMe
            if not inv.Nvme.IsEmpty then
                heading "NVMe Devices 💽"
                let rows =
                    inv.Nvme |> List.map (fun n ->
                        [ n.Name; Fmt.str n.Model; Fmt.str n.Capacity; Fmt.boolYN n.TrimSupport; Fmt.str n.SmartStatus ])
                sb.Append(table [ "Name"; "Model"; "Capacity"; "TRIM"; "SMART" ] rows) |> ignore

            // Network
            if not inv.NetworkMounts.IsEmpty then
                heading "Network Mounts 🌐"
                let rows =
                    inv.NetworkMounts |> List.map (fun n ->
                        [ n.Source; n.MountPoint; n.FsType ])
                sb.Append(table [ "Source"; "Mount"; "Type" ] rows) |> ignore

            // Warnings
            heading "Warnings"
            if inv.Warnings.IsEmpty then
                sb.Append "🟢 No warnings.\n" |> ignore
            else
                for w in inv.Warnings do
                    sb.Append(sprintf "%s [%s] %s\n" (Fmt.severityIcon w.Severity) w.Category w.Message) |> ignore

            sb.ToString()

    // ---------------------------------------------------------------
    // JSON export.
    // ---------------------------------------------------------------
    module Json =

        let private volume (v: Volume) =
            JObj
                [ "deviceId", JStr v.DeviceId
                  "name", jStrO v.Name
                  "mountPoint", jStrO v.MountPoint
                  "fileSystem", jStrO v.FileSystem
                  "sizeBytes", jIntO v.SizeBytes
                  "usedBytes", jIntO v.UsedBytes
                  "availBytes", jIntO v.AvailBytes
                  "capacityPercent", jIntO32 v.CapacityPercent
                  "readOnly", JBool v.ReadOnly ]

        let private disk (d: PhysicalDisk) =
            JObj
                [ "deviceId", JStr d.DeviceId
                  "mediaName", jStrO d.MediaName
                  "sizeBytes", jIntO d.SizeBytes
                  "internal", jBoolO d.Internal
                  "solidState", jBoolO d.SolidState
                  "busProtocol", jStrO d.BusProtocol
                  "linkSpeed", jStrO d.LinkSpeed
                  "virtual", jBoolO d.Virtual
                  "removable", jBoolO d.Removable
                  "smartStatus", jStrO d.SmartStatus
                  "volumes", JArr(d.Volumes |> List.map volume) ]

        let render (inv: Inventory) : string =
            JObj
                [ "timestamp", JStr(Fmt.iso inv.Timestamp)
                  "computer",
                    JObj
                        [ "hostName", JStr inv.Computer.HostName
                          "model", jStrO inv.Computer.Model
                          "osVersion", jStrO inv.Computer.OsVersion ]
                  "disks", JArr(inv.Disks |> List.map disk)
                  "usb",
                    JArr(
                        inv.Usb
                        |> List.map (fun u ->
                            JObj
                                [ "name", JStr u.Name
                                  "manufacturer", jStrO u.Manufacturer
                                  "speed", jStrO u.Speed
                                  "serialNumber", jStrO u.SerialNumber
                                  "vendor", jStrO u.Vendor ]))
                  "thunderbolt",
                    JArr(
                        inv.Thunderbolt
                        |> List.map (fun t ->
                            JObj
                                [ "name", JStr t.Name
                                  "vendor", jStrO t.Vendor
                                  "speed", jStrO t.Speed ]))
                  "nvme",
                    JArr(
                        inv.Nvme
                        |> List.map (fun n ->
                            JObj
                                [ "name", JStr n.Name
                                  "model", jStrO n.Model
                                  "capacity", jStrO n.Capacity
                                  "trimSupport", jBoolO n.TrimSupport
                                  "smartStatus", jStrO n.SmartStatus ]))
                  "networkMounts",
                    JArr(
                        inv.NetworkMounts
                        |> List.map (fun n ->
                            JObj
                                [ "source", JStr n.Source
                                  "mountPoint", JStr n.MountPoint
                                  "fsType", JStr n.FsType ]))
                  "fsUsage",
                    JArr(
                        inv.FsUsage
                        |> List.map (fun f ->
                            JObj
                                [ "filesystem", JStr f.Filesystem
                                  "mountPoint", JStr f.MountPoint
                                  "sizeBytes", jIntO f.SizeBytes
                                  "usedBytes", jIntO f.UsedBytes
                                  "availBytes", jIntO f.AvailBytes
                                  "capacityPercent", jIntO32 f.CapacityPercent ]))
                  "warnings",
                    JArr(
                        inv.Warnings
                        |> List.map (fun w ->
                            JObj
                                [ "severity", JStr(Fmt.severityText w.Severity)
                                  "category", JStr w.Category
                                  "message", JStr w.Message
                                  "device", jStrO w.Device ])) ]
            |> Jout.toString

    // ---------------------------------------------------------------
    // CSV export (flattened disk/volume view).
    // ---------------------------------------------------------------
    module Csv =

        /// Neutralise spreadsheet formula injection: a cell beginning with
        /// =, +, @, or a non-numeric - is prefixed with an apostrophe so
        /// Excel/Numbers treat it as text rather than a formula. The "-"
        /// placeholder and negative numbers are left untouched.
        let private csvSafe (s: string) =
            let dangerous =
                s.Length > 0
                && (let c0 = s.[0] in
                    c0 = '=' || c0 = '+' || c0 = '@'
                    || (c0 = '-' && s.Length > 1 && not (Char.IsDigit s.[1])))
            if dangerous then "'" + s else s

        let private q (s: string) =
            let s = csvSafe s
            if s.Contains "," || s.Contains "\"" || s.Contains "\n" then
                "\"" + s.Replace("\"", "\"\"") + "\""
            else s

        let render (inv: Inventory) : string =
            let sb = StringBuilder()
            sb.Append "DiskDevice,MediaName,BusProtocol,LinkSpeed,Internal,SolidState,SmartStatus,VolumeDevice,VolumeName,MountPoint,FileSystem,SizeBytes,UsedBytes,AvailBytes,CapacityPercent,ReadOnly\n"
            |> ignore
            let o (x: string option) = Fmt.str x
            let ob (x: bool option) = Fmt.boolYN x
            let oi (x: int64 option) = match x with Some v -> string v | None -> ""
            let op (x: int option) = match x with Some v -> string v | None -> ""
            for d in inv.Disks do
                if d.Volumes.IsEmpty then
                    sb.Append(
                        String.Join(",",
                            [ q d.DeviceId; q (o d.MediaName); q (o d.BusProtocol); q (o d.LinkSpeed)
                              ob d.Internal; ob d.SolidState; q (o d.SmartStatus)
                              ""; ""; ""; ""; ""; ""; ""; ""; "" ]))
                    |> ignore
                    sb.Append '\n' |> ignore
                else
                    for v in d.Volumes do
                        sb.Append(
                            String.Join(",",
                                [ q d.DeviceId; q (o d.MediaName); q (o d.BusProtocol); q (o d.LinkSpeed)
                                  ob d.Internal; ob d.SolidState; q (o d.SmartStatus)
                                  q v.DeviceId; q (o v.Name); q (o v.MountPoint); q (o v.FileSystem)
                                  oi v.SizeBytes; oi v.UsedBytes; oi v.AvailBytes; op v.CapacityPercent
                                  (if v.ReadOnly then "Yes" else "No") ]))
                        |> ignore
                        sb.Append '\n' |> ignore
            sb.ToString()

    // ---------------------------------------------------------------
    // Markdown export.
    // ---------------------------------------------------------------
    module Markdown =

        let render (inv: Inventory) : string =
            let sb = StringBuilder()
            let ln (s: string) = sb.Append(s).Append '\n' |> ignore
            ln "# Mac Disk Explorer Report"
            ln ""
            ln (sprintf "- **Host:** %s" inv.Computer.HostName)
            ln (sprintf "- **Model:** %s" (Fmt.str inv.Computer.Model))
            ln (sprintf "- **macOS:** %s" (Fmt.str inv.Computer.OsVersion))
            ln (sprintf "- **Generated:** %s" (Fmt.iso inv.Timestamp))
            ln ""
            ln "## Summary"
            ln ""
            ln "| Metric | Count |"
            ln "|---|---|"
            ln (sprintf "| Physical disks | %d |" inv.Disks.Length)
            ln (sprintf "| Volumes | %d |" (allVolumes inv).Length)
            ln (sprintf "| USB devices | %d |" inv.Usb.Length)
            ln (sprintf "| Thunderbolt | %d |" inv.Thunderbolt.Length)
            ln (sprintf "| NVMe | %d |" inv.Nvme.Length)
            ln (sprintf "| Network mounts | %d |" inv.NetworkMounts.Length)
            ln (sprintf "| Warnings | %d |" inv.Warnings.Length)
            ln ""
            ln "## Physical Disks"
            ln ""
            let mdIntN, mdExtN = Stats.internalExternal inv.Disks
            let mdSmartBad = Stats.smartFailing inv.Disks
            ln (sprintf "%s%d internal · %d external · %s total"
                    (if mdSmartBad > 0 then sprintf "🔴 %d SMART failing · " mdSmartBad else "")
                    mdIntN mdExtN (Fmt.bytes (Some(Stats.totalDiskBytes inv.Disks))))
            ln ""
            ln "| Device | Media | Size | Bus | Link | Location | Type | SMART |"
            ln "|---|---|---|---|---|---|---|---|"
            for d in inv.Disks do
                let kind, icon = Fmt.classifyBus d.BusProtocol d.Internal
                ln (sprintf "| %s | %s | %s | %s %s | %s | %s | %s | %s |"
                        d.DeviceId (Fmt.str d.MediaName) (Fmt.bytes d.SizeBytes) icon kind
                        (Fmt.str d.LinkSpeed)
                        (if d.Internal = Some true then "Internal" else "External")
                        (if d.SolidState = Some true then "SSD" else "HDD")
                        (Fmt.str d.SmartStatus))
            ln ""
            ln "## Volumes"
            ln ""
            let mdVols = allVolumes inv
            let mdVolCrit = Stats.countVolumesAt Critical mdVols
            let mdVolWarn = Stats.countVolumesAt Warning mdVols
            let mdWorst =
                Stats.worstVolume mdVols
                |> Option.map (fun v ->
                    sprintf "%s %d%%" (Stats.volumeLabel v) (v.CapacityPercent |> Option.defaultValue 0))
                |> Option.defaultValue "n/a"
            ln (sprintf "%s%s%d mounted · worst: %s"
                    (if mdVolCrit > 0 then sprintf "🔴 %d critical · " mdVolCrit else "")
                    (if mdVolWarn > 0 then sprintf "🟡 %d warning · " mdVolWarn else "")
                    (Stats.mountedCount mdVols) mdWorst)
            ln ""
            ln "| | Device | Name | Mount | FS | Size | Used | Full | Mode |"
            ln "|---|---|---|---|---|---|---|---|---|"
            for v in allVolumes inv do
                ln (sprintf "| %s | %s | %s | %s | %s | %s | %s | %s | %s |"
                        (Fmt.healthIcon v.CapacityPercent) v.DeviceId (Fmt.str v.Name)
                        (Fmt.str v.MountPoint) (Fmt.str v.FileSystem) (Fmt.bytes v.SizeBytes)
                        (Fmt.bytes v.UsedBytes) (Fmt.pct v.CapacityPercent)
                        (if v.ReadOnly then "RO" else "RW"))
            ln ""
            if not inv.Usb.IsEmpty then
                ln "## USB Devices"
                ln ""
                ln "| Name | Manufacturer | Speed |"
                ln "|---|---|---|"
                for u in inv.Usb do
                    ln (sprintf "| %s | %s | %s |" u.Name (Fmt.str u.Manufacturer) (Fmt.str u.Speed))
                ln ""
            if not inv.Nvme.IsEmpty then
                ln "## NVMe Devices"
                ln ""
                ln "| Name | Model | Capacity | TRIM | SMART |"
                ln "|---|---|---|---|---|"
                for n in inv.Nvme do
                    ln (sprintf "| %s | %s | %s | %s | %s |"
                            n.Name (Fmt.str n.Model) (Fmt.str n.Capacity) (Fmt.boolYN n.TrimSupport) (Fmt.str n.SmartStatus))
                ln ""
            if not inv.NetworkMounts.IsEmpty then
                ln "## Network Mounts"
                ln ""
                ln "| Source | Mount | Type |"
                ln "|---|---|---|"
                for n in inv.NetworkMounts do
                    ln (sprintf "| %s | %s | %s |" n.Source n.MountPoint n.FsType)
                ln ""
            ln "## Warnings"
            ln ""
            if inv.Warnings.IsEmpty then
                ln "🟢 No warnings."
            else
                let crit = Stats.countSeverity Critical inv.Warnings
                let warn = Stats.countSeverity Warning inv.Warnings
                let info = Stats.countSeverity Info inv.Warnings
                ln (sprintf "🔴 %d critical · 🟡 %d warning · 🔵 %d info" crit warn info)
                ln ""
                for w in inv.Warnings do
                    ln (sprintf "- %s **%s** — %s" (Fmt.severityIcon w.Severity) w.Category w.Message)
            sb.ToString()

    // ---------------------------------------------------------------
    // HTML dashboard (self-contained; no external assets).
    // ---------------------------------------------------------------
    module Html =

        let private esc (s: string) =
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

        let private eo (s: string option) = esc (Fmt.str s)

        /// Colored capacity bar (CSS only).
        // Static assets live as module constants so `render` stays focused
        // on content assembly rather than styling/scripting. NOTE: kept as
        // regular (not triple-quoted) strings on purpose — the sort script
        // relies on the "\\-" escape resolving to a single backslash.
        let private cssBlock = "<style>
:root{
  --bg:#f6f7f9; --panel:#ffffff; --text:#1c2128; --muted:#6b7280;
  --border:#e5e7eb; --accent:#2563eb; --accent-soft:#eff6ff;
  --ok:#16a34a; --warn:#d97706; --crit:#dc2626; --shadow:0 1px 3px rgba(0,0,0,.08);
  /* Soft fills + darkened inks so small-caps chip text still clears 4.5:1. */
  --ok-soft:#f0fdf4; --warn-soft:#fffbeb; --crit-soft:#fef2f2;
  --ok-ink:#166534; --warn-ink:#92400e; --crit-ink:#991b1b;
}
:root[data-theme=dark]{
  --bg:#0d1117; --panel:#161b22; --text:#e6edf3; --muted:#8b949e;
  --border:#30363d; --accent:#58a6ff; --accent-soft:#132033;
  --ok:#3fb950; --warn:#d29922; --crit:#f85149; --shadow:0 1px 3px rgba(0,0,0,.4);
  --ok-soft:#0d2417; --warn-soft:#2b2008; --crit-soft:#2a1215;
  --ok-ink:#86efac; --warn-ink:#fcd34d; --crit-ink:#fca5a5;
}
*{box-sizing:border-box}
body{margin:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
  background:var(--bg);color:var(--text);line-height:1.45;padding-bottom:4rem}
header{position:sticky;top:0;z-index:20;background:var(--panel);border-bottom:1px solid var(--border);
  box-shadow:var(--shadow);padding:.85rem 1.25rem;display:flex;align-items:center;gap:1rem;flex-wrap:wrap}
header h1{font-size:1.05rem;margin:0;font-weight:650}
header .meta{color:var(--muted);font-size:.8rem}
header .spacer{flex:1}
#search{padding:.5rem .75rem;border:1px solid var(--border);border-radius:8px;background:var(--bg);
  color:var(--text);min-width:220px;font-size:.85rem}
.btn{cursor:pointer;border:1px solid var(--border);background:var(--bg);color:var(--text);
  padding:.5rem .8rem;border-radius:8px;font-size:.85rem}
.btn:hover{border-color:var(--accent)}
main{max-width:1200px;margin:0 auto;padding:1.25rem}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:.85rem;margin-bottom:1.5rem}
.card{background:var(--panel);border:1px solid var(--border);border-radius:12px;padding:1rem;
  box-shadow:var(--shadow)}
.card .ico,.card .num,.card .lbl,.card .sub{display:block}
.card .ico{font-size:1.4rem}
.card .num{font-size:1.9rem;font-weight:700;line-height:1}
.card .lbl{color:var(--muted);font-size:.78rem;text-transform:uppercase;letter-spacing:.03em;margin-top:.25rem}
/* Cards that focus a panel. Neutralise the button UA styles but keep the
   card surface; hierarchy stays with the card, not the control. */
button.card{font:inherit;text-align:left;cursor:pointer;width:100%;color:inherit;
  -webkit-appearance:none;appearance:none}
button.card:hover{border-color:var(--accent)}
button.card[aria-pressed=true]{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}
button.card[aria-pressed=true] .lbl::after{content:' · focused';color:var(--accent)}
.chip-filter{background:var(--accent-soft);color:var(--accent);border-color:var(--accent)}
/* The verdict card earns width as well as position when it has something to
   report. Guarded so it can never span more columns than the grid has. */
@media (min-width:480px){.cards .card-alert{grid-column:span 2}}
.card .sub{margin-top:.5rem;padding-top:.5rem;border-top:1px solid var(--border);
  font-size:.72rem;color:var(--muted);line-height:1.35}
/* Consequence-weight: the critical card only takes visual priority when it
   has something to report. Full border + tinted fill, never a side stripe. */
.card-alert{background:var(--crit-soft);border-color:var(--crit)}
.card-alert .num{color:var(--crit-ink)}
.card-alert .lbl,.card-alert .sub{color:var(--crit-ink)}
.card-alert .sub{border-top-color:var(--crit)}
details{background:var(--panel);border:1px solid var(--border);border-radius:12px;margin-bottom:1.1rem;
  box-shadow:var(--shadow);scroll-margin-top:72px}
details>summary{cursor:pointer;padding:.85rem 1.1rem;font-weight:600;list-style:none;
  display:flex;align-items:center;gap:.5rem;user-select:none;border-radius:12px}
details>summary::-webkit-details-marker{display:none}
details>summary::marker{content:''}
details>summary::before{content:'▸';color:var(--muted);transition:transform .15s}
details[open]>summary::before{transform:rotate(90deg)}
/* Summary-line facts. Visible while the panel is collapsed, so severity
   never hides behind a disclosure. */
.sumbar{display:flex;flex-wrap:wrap;align-items:center;gap:.35rem;margin-left:auto;font-weight:400}
.chip{display:inline-flex;align-items:center;gap:.25rem;padding:.1rem .45rem;border-radius:999px;
  font-size:.72rem;font-weight:600;background:var(--bg);color:var(--muted);
  border:1px solid var(--border);white-space:nowrap}
/* Author display rules beat the UA [hidden] rule, so restore it explicitly
   or a hidden chip renders as an empty pill. */
.chip[hidden]{display:none}
.chip-crit{background:var(--crit-soft);color:var(--crit-ink);border-color:var(--crit)}
.chip-warn{background:var(--warn-soft);color:var(--warn-ink);border-color:var(--warn)}
.chip-ok{background:var(--ok-soft);color:var(--ok-ink);border-color:var(--ok)}
.tblwrap{overflow-x:auto}
table{width:100%;border-collapse:collapse;font-size:.85rem}
thead th{background:var(--panel);text-align:left;border-bottom:2px solid var(--border);white-space:nowrap}
thead th::after{font-size:.7rem;color:var(--muted)}
th.sort-asc::after{content:' ▲'}
th.sort-desc::after{content:' ▼'}
/* The sort control is a real button so it is focusable and operable via
   keyboard; the th keeps its columnheader semantics and aria-sort state. */
th>.sortbtn{all:unset;box-sizing:border-box;display:block;width:100%;cursor:pointer;
  padding:.6rem .8rem;font:inherit;font-weight:600;color:inherit}
th>.sortbtn:hover{color:var(--accent)}
/* Column aggregate sits outside the button so the sort control's accessible
   name stays just the column label. */
th.hasagg>.sortbtn{padding-bottom:.1rem}
th>.colagg{display:block;padding:0 .8rem .5rem;font-size:.7rem;font-weight:400;
  color:var(--muted);white-space:nowrap}
:where(a,button,summary,input,[tabindex]):focus-visible{outline:2px solid var(--accent);
  outline-offset:2px;border-radius:6px}
tbody td{padding:.55rem .8rem;border-bottom:1px solid var(--border);white-space:nowrap}
tbody tr:hover{background:var(--accent-soft)}
.muted{color:var(--muted)}
.bar{position:relative;background:var(--bg);border:1px solid var(--border);border-radius:6px;
  height:18px;min-width:120px;overflow:hidden}
.bar .fill{height:100%;border-radius:5px 0 0 5px}
.bar .fill.ok{background:var(--ok)}
.bar .fill.warn{background:var(--warn)}
.bar .fill.crit{background:var(--crit)}
.bar .barlabel{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
  font-size:.72rem;font-weight:600;mix-blend-mode:difference;color:#fff}
.badge{display:inline-block;padding:.12rem .5rem;border-radius:999px;font-size:.72rem;font-weight:600}
.badge.int{background:var(--accent-soft);color:var(--accent)}
.badge.ext{background:rgba(217,119,6,.15);color:var(--warn)}
.warnlist{list-style:none;margin:0;padding:.5rem 1.1rem 1rem}
.warnlist li{padding:.55rem .75rem;border-radius:8px;margin-top:.5rem;border-left:4px solid var(--border);
  background:var(--bg)}
.warnlist li.crit{border-left-color:var(--crit)}
.warnlist li.warn{border-left-color:var(--warn)}
.warnlist li.info{border-left-color:var(--accent)}
.warnlist .cat{font-weight:600;margin-right:.4rem}
footer{max-width:1200px;margin:0 auto;padding:1rem 1.25rem;color:var(--muted);font-size:.78rem}
</style></head><body>"

        let private jsBlock = "<script>
(function(){
  var root=document.documentElement;
  var saved=localStorage.getItem('mde-theme');
  if(saved){root.setAttribute('data-theme',saved);}
  else if(window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches){root.setAttribute('data-theme','dark');}
  document.getElementById('themeBtn').addEventListener('click',function(){
    var cur=root.getAttribute('data-theme')==='dark'?'light':'dark';
    root.setAttribute('data-theme',cur);localStorage.setItem('mde-theme',cur);
  });
  // Search filter across every table row.
  var search=document.getElementById('search');
  search.addEventListener('input',function(){
    var q=this.value.toLowerCase();
    document.querySelectorAll('tbody tr').forEach(function(tr){
      tr.style.display = tr.textContent.toLowerCase().indexOf(q)>-1 ? '' : 'none';
    });
  });
  // Card focus mode: activating a card opens its panel and closes the other
  // sections. Activating the focused card again restores them. Only panels
  // marked data-section participate, so the raw-JSON panel is never touched.
  var liveCards=document.querySelectorAll('.card-live[data-target]');
  var sections=document.querySelectorAll('details[data-section]');
  var warnNote=document.getElementById('warnfilter');
  // A severity card narrows the warning list to its own severity, so the card
  // shows what it promises rather than every finding.
  function applyWarnFilter(filter){
    document.querySelectorAll('.warnlist li').forEach(function(li){
      li.style.display=(!filter||li.classList.contains(filter))?'':'none';
    });
    if(warnNote){
      warnNote.hidden=!filter;
      warnNote.textContent=filter?('showing '+(filter==='crit'?'critical':'advisories')+' only'):'';
    }
  }
  function clearFocus(){
    liveCards.forEach(function(c){c.setAttribute('aria-pressed','false');});
    applyWarnFilter(null);
  }
  liveCards.forEach(function(card){
    card.addEventListener('click',function(){
      var target=document.getElementById(card.getAttribute('data-target'));
      if(!target){return;}
      var wasFocused=card.getAttribute('aria-pressed')==='true';
      clearFocus();
      if(wasFocused){
        sections.forEach(function(d){d.open=true;});   // restore the full report
        return;
      }
      sections.forEach(function(d){d.open=(d===target);});
      applyWarnFilter(card.getAttribute('data-filter'));
      card.setAttribute('aria-pressed','true');
      var reduce=window.matchMedia&&window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      target.scrollIntoView({behavior:reduce?'auto':'smooth',block:'start'});
    });
  });
  // Manually toggling any panel exits focus mode, so the pressed state never
  // claims a filtering that is no longer in effect.
  sections.forEach(function(d){
    d.addEventListener('toggle',function(){
      if(!d.open){return;}
      var anyPressed=[].some.call(liveCards,function(c){return c.getAttribute('aria-pressed')==='true';});
      if(!anyPressed){return;}
      var openCount=[].filter.call(sections,function(s){return s.open;}).length;
      if(openCount>1){clearFocus();}
    });
  });

  // Sortable columns. The listener lives on the <button> inside each <th>,
  // so Enter/Space work natively and aria-sort reflects the current state.
  document.querySelectorAll('table thead th .sortbtn').forEach(function(btn){
    btn.addEventListener('click',function(){
      var th=btn.closest('th');
      var table=th.closest('table');
      var idx=Array.prototype.indexOf.call(th.parentNode.children,th);
      var asc=th.getAttribute('aria-sort')!=='ascending';
      table.querySelectorAll('thead th').forEach(function(o){
        o.classList.remove('sort-asc','sort-desc');o.setAttribute('aria-sort','none');
      });
      th.classList.add(asc?'sort-asc':'sort-desc');
      th.setAttribute('aria-sort',asc?'ascending':'descending');
      var tbody=table.querySelector('tbody');
      var rows=Array.prototype.slice.call(tbody.querySelectorAll('tr'));
      rows.sort(function(a,b){
        var x=(a.children[idx]?a.children[idx].textContent:'').trim();
        var y=(b.children[idx]?b.children[idx].textContent:'').trim();
        var nx=parseFloat(x.replace(/[^0-9.\\-]/g,'')), ny=parseFloat(y.replace(/[^0-9.\\-]/g,''));
        var bothNum=!isNaN(nx)&&!isNaN(ny)&&x.match(/[0-9]/)&&y.match(/[0-9]/);
        var cmp = bothNum ? (nx-ny) : x.localeCompare(y);
        return asc?cmp:-cmp;
      });
      rows.forEach(function(r){tbody.appendChild(r);});
    });
  });
})();
</script></body></html>"

        /// Stable DOM id for a panel, derived from its title so cards and
        /// panels can never drift apart.
        let private panelId (title: string) =
            "panel-"
            + (title.ToLowerInvariant()
               |> String.map (fun c -> if Char.IsLetterOrDigit c then c else '-'))

        /// A summary tile. `Rank` encodes consequence (lower leads): the
        /// verdict cards come first, then the surfaces a triage read actually
        /// depends on, then peripheral inventory. `Target` is the panel this
        /// card focuses when activated; None means no panel was rendered for
        /// it (an empty category), so the card stays non-interactive rather
        /// than becoming a dead control.
        type private SummaryCard =
            { Label: string
              Value: string
              Icon: string
              Sub: string option
              Alert: bool
              Rank: int
              Target: string option
              /// Severity class this card narrows its target panel to, so a
              /// card labelled "Advisories" shows advisories rather than
              /// every finding. None means "show the panel unfiltered".
              Filter: string option }

        /// Consequence ordering. Verdict cards (rank <= 1) hold their position
        /// even at zero, because "no criticals" is itself the headline finding.
        /// Inventory cards with nothing to report sink below those with data,
        /// so an empty "USB 0" never sits between the reader and the verdict.
        let private byConsequence (cards: SummaryCard list) =
            cards
            |> List.sortBy (fun c ->
                let inertInventory = c.Rank > 1 && c.Value = "0"
                (if inertInventory then 1 else 0), c.Rank)

        let private capBar (pct: int option) =
            match pct with
            | None -> "<span class=\"muted\">-</span>"
            | Some p ->
                let cls = if p >= 95 then "crit" elif p >= 90 then "warn" else "ok"
                sprintf "<div class=\"bar\"><div class=\"fill %s\" style=\"width:%d%%\"></div><span class=\"barlabel\">%d%%</span></div>" cls p p

        let render (inv: Inventory) : string =
            let sb = StringBuilder()
            let a (s: string) = sb.Append s |> ignore

            // ---- Shared aggregates (used by cards, summary lines, and thead) ----
            let vols = allVolumes inv
            let critCount = Stats.countSeverity Critical inv.Warnings
            let warnCount = Stats.countSeverity Warning inv.Warnings
            let volCrit = Stats.countVolumesAt Critical vols
            let volWarn = Stats.countVolumesAt Warning vols
            let worstVol = Stats.worstVolume vols
            let intN, extN = Stats.internalExternal inv.Disks
            let smartBad = Stats.smartFailing inv.Disks
            let totalDisk = Stats.totalDiskBytes inv.Disks

            /// "Data 99%" for the fullest volume, when one is known.
            let worstVolText =
                worstVol
                |> Option.map (fun v ->
                    sprintf "%s %d%%" (Stats.volumeLabel v) (v.CapacityPercent |> Option.defaultValue 0))

            /// Up to `n` distinct subjects of warnings at a given severity.
            let subjectsAt (sev: Severity) (n: int) =
                inv.Warnings
                |> List.filter (fun w -> w.Severity = sev)
                |> List.map (fun w ->
                    // Prefer the volume name already embedded in the message.
                    match w.Message.Split(" is ") with
                    | [| head; _ |] -> head
                    | _ -> w.Device |> Option.defaultValue w.Category)
                |> List.distinct
                |> List.truncate n

            let joinOr (fallback: string) (xs: string list) =
                if xs.IsEmpty then fallback else String.Join(", ", xs)

            // ---- Summary-line chip helpers ----
            // Every severity chip carries glyph + number + word, so status is
            // never conveyed by hue alone (PRODUCT.md accessibility rule).
            let chip (cls: string) (text: string) =
                if cls = "" then sprintf "<span class=\"chip\">%s</span>" (esc text)
                else sprintf "<span class=\"chip chip-%s\">%s</span>" cls (esc text)

            let sevChip (sev: Severity) (n: int) =
                if n <= 0 then None
                else
                    let cls, word =
                        match sev with
                        | Critical -> "crit", "critical"
                        | Warning -> "warn", "warning"
                        | Info -> "", "info"
                    Some(chip cls (sprintf "%s %d %s" (Fmt.severityIcon sev) n word))

            let plain text = Some(chip "" text)

            // Summary cards, declared in consequence order and sorted by it so
            // the verdict leads and inert inventory sinks (PRODUCT.md: "lead
            // with the verdict").
            let cards =
                byConsequence
                    // Severity cards narrow the Warnings panel to their own
                    // severity, and go inert at zero: focusing a panel that
                    // would show none of the card's own findings is a broken
                    // promise, not a shortcut.
                    [ { Label = "Critical"; Value = string critCount; Icon = "🔴"; Rank = 0
                        Sub = (if critCount > 0 then Some(joinOr "-" (subjectsAt Critical 3)) else Some "none")
                        Alert = (critCount > 0)
                        Target = (if critCount > 0 then Some(panelId "Warnings") else None)
                        Filter = Some "crit" }
                      // Counts Severity=Warning only. Named "Advisories" so it
                      // does not read as a contradiction of the Warnings panel,
                      // which counts every finding regardless of severity.
                      { Label = "Advisories"; Value = string warnCount; Icon = "🟡"; Rank = 1
                        Sub = (if warnCount > 0 then Some(joinOr "-" (subjectsAt Warning 3)) else Some "none")
                        Alert = false
                        Target = (if warnCount > 0 then Some(panelId "Warnings") else None)
                        Filter = Some "warn" }
                      { Label = "Volumes"; Value = string vols.Length; Icon = "🗂️"; Rank = 2
                        Sub = Some(sprintf "%d mounted · worst %s" (Stats.mountedCount vols)
                                       (worstVolText |> Option.defaultValue "n/a"))
                        Alert = false; Target = Some(panelId "Volumes")
                        Filter = None }
                      { Label = "Physical Disks"; Value = string inv.Disks.Length; Icon = "💽"; Rank = 3
                        Sub = Some(sprintf "%d internal · %d external · %s total" intN extN (Fmt.bytes (Some totalDisk)))
                        Alert = false; Target = Some(panelId "Physical Disks")
                        Filter = None }
                      { Label = "NVMe"; Value = string inv.Nvme.Length; Icon = "💽"; Rank = 4
                        Sub = (inv.Nvme |> List.tryHead |> Option.map (fun n -> sprintf "TRIM %s" (Fmt.boolYN n.TrimSupport)))
                        Alert = false
                        Target = (if inv.Nvme.IsEmpty then None else Some(panelId "NVMe Devices"))
                        Filter = None }
                      { Label = "Thunderbolt"; Value = string inv.Thunderbolt.Length; Icon = "⚡"; Rank = 5
                        Sub = None; Alert = false
                        Target = (if inv.Thunderbolt.IsEmpty then None else Some(panelId "Thunderbolt Devices"))
                        Filter = None }
                      { Label = "USB"; Value = string inv.Usb.Length; Icon = "🔌"; Rank = 6
                        Sub = None; Alert = false
                        Target = (if inv.Usb.IsEmpty then None else Some(panelId "USB Devices"))
                        Filter = None }
                      { Label = "Network Mounts"; Value = string inv.NetworkMounts.Length; Icon = "🌐"; Rank = 7
                        Sub = None; Alert = false
                        Target = (if inv.NetworkMounts.IsEmpty then None else Some(panelId "Network Mounts"))
                        Filter = None } ]

            // ---- Head + styles ----
            a "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            a "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            a (sprintf "<title>Mac Disk Explorer — %s</title>" (esc inv.Computer.HostName))
            a cssBlock

            // ---- Header ----
            a "<header>"
            a (sprintf "<h1>💽 %s</h1>" (esc inv.Computer.HostName))
            a (sprintf "<span class=\"meta\">%s · macOS %s · %s</span>"
                   (eo inv.Computer.Model) (eo inv.Computer.OsVersion) (esc (Fmt.iso inv.Timestamp)))
            a "<span class=\"spacer\"></span>"
            a "<input id=\"search\" type=\"search\" placeholder=\"🔍 Filter all tables…\">"
            a "<button class=\"btn\" id=\"themeBtn\" title=\"Toggle theme\">🌓 Theme</button>"
            a "</header><main>"

            // ---- Cards ----
            a "<div class=\"cards\">"
            for c in cards do
                let alertCls = if c.Alert then " card-alert" else ""
                // Cards that focus a panel are real buttons (keyboard-operable,
                // aria-pressed reflects focus state). Internals are spans so the
                // button's phrasing-content model stays valid.
                match c.Target with
                | Some target ->
                    let filterAttr =
                        match c.Filter with
                        | Some f -> sprintf " data-filter=\"%s\"" f
                        | None -> ""
                    a (sprintf "<button type=\"button\" class=\"card card-live%s\" data-target=\"%s\"%s aria-pressed=\"false\" title=\"Show only this section\">"
                           alertCls target filterAttr)
                | None ->
                    a (sprintf "<div class=\"card%s\">" alertCls)
                a (sprintf "<span class=\"ico\">%s</span><span class=\"num\">%s</span><span class=\"lbl\">%s</span>"
                       c.Icon c.Value (esc c.Label))
                match c.Sub with
                | Some s -> a (sprintf "<span class=\"sub\">%s</span>" (esc s))
                | None -> ()
                a (if c.Target.IsSome then "</button>" else "</div>")
            a "</div>"

            // ---- Warnings ----
            a (sprintf "<details open id=\"%s\" data-section><summary>⚠️ Warnings" (panelId "Warnings"))
            a (sprintf " <span class=\"muted\">(%d)</span>" inv.Warnings.Length)
            let warnMeta =
                [ sevChip Critical critCount
                  sevChip Warning warnCount
                  sevChip Info (Stats.countSeverity Info inv.Warnings) ]
                |> List.choose id
            a "<span class=\"sumbar\">"
            // Announces an active severity filter so a reduced row count is
            // never mistaken for missing data.
            a "<span class=\"chip chip-filter\" id=\"warnfilter\" hidden></span>"
            for m in warnMeta do a m
            a "</span>"
            a "</summary>"
            if inv.Warnings.IsEmpty then
                a "<ul class=\"warnlist\"><li class=\"info\">🟢 No warnings detected.</li></ul>"
            else
                a "<ul class=\"warnlist\">"
                for w in inv.Warnings do
                    let cls = match w.Severity with Critical -> "crit" | Warning -> "warn" | Info -> "info"
                    a (sprintf "<li class=\"%s\">%s <span class=\"cat\">%s</span>%s</li>"
                           cls (Fmt.severityIcon w.Severity) (esc w.Category) (esc w.Message))
                a "</ul>"
            a "</details>"

            // Helper to emit a sortable, collapsible table section. Uses a real
            // <details>/<summary> disclosure (natively keyboard-operable) and a
            // <button> inside each <th> so sorting is reachable without a mouse.
            // `meta` renders into the summary line so it stays readable while
            // the panel is collapsed; `headers` may carry a per-column aggregate.
            let sectionTable (title: string) (icon: string) (count: int) (meta: string list)
                             (headers: (string * string option) list) (rows: string list) =
                a (sprintf "<details open id=\"%s\" data-section><summary>%s %s <span class=\"muted\">(%d)</span>"
                       (panelId title) icon (esc title) count)
                if not meta.IsEmpty then
                    a "<span class=\"sumbar\">"
                    for m in meta do a m
                    a "</span>"
                a "</summary>"
                a "<div class=\"tblwrap\"><table><thead><tr>"
                for (h, agg) in headers do
                    match agg with
                    | Some g ->
                        a (sprintf "<th scope=\"col\" aria-sort=\"none\" class=\"hasagg\"><button type=\"button\" class=\"sortbtn\">%s</button><span class=\"colagg\">%s</span></th>"
                               (esc h) (esc g))
                    | None ->
                        a (sprintf "<th scope=\"col\" aria-sort=\"none\"><button type=\"button\" class=\"sortbtn\">%s</button></th>" (esc h))
                a "</tr></thead><tbody>"
                for r in rows do a r
                a "</tbody></table></div></details>"

            // ---- Physical disks ----
            let diskRows =
                inv.Disks
                |> List.map (fun d ->
                    let kind, bico = Fmt.classifyBus d.BusProtocol d.Internal
                    let locBadge =
                        if d.Internal = Some true then "<span class=\"badge int\">💽 Internal</span>"
                        else "<span class=\"badge ext\">External</span>"
                    sprintf "<tr><td>%s</td><td>%s</td><td>%s</td><td>%s %s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                        (esc d.DeviceId) (eo d.MediaName) (Fmt.bytes d.SizeBytes) bico (esc kind)
                        (eo d.LinkSpeed)
                        locBadge (if d.SolidState = Some true then "SSD" else "HDD") (eo d.SmartStatus))
            let diskMeta =
                [ sevChip Critical smartBad
                  plain (sprintf "%d internal · %d external" intN extN)
                  plain (sprintf "%s total" (Fmt.bytes (Some totalDisk))) ]
                |> List.choose id
            sectionTable "Physical Disks" "💽" inv.Disks.Length diskMeta
                [ "Device", None
                  "Media", None
                  // Whole-disk sizes are additive, so a real total is honest here.
                  "Size", Some(sprintf "%s total" (Fmt.bytes (Some totalDisk)))
                  "Bus", None
                  "Link", (let known = inv.Disks |> List.filter (fun d -> d.LinkSpeed.IsSome) |> List.length
                           if known > 0 then Some(sprintf "%d reported" known) else None)
                  "Location", Some(sprintf "%d int · %d ext" intN extN)
                  "Type", None
                  "SMART", (if smartBad > 0 then Some(sprintf "%d failing" smartBad) else Some "all verified") ]
                diskRows

            // ---- Volumes ----
            let volRows =
                vols
                |> List.map (fun v ->
                    sprintf "<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                        (Fmt.healthIcon v.CapacityPercent) (esc v.DeviceId) (eo v.Name) (eo v.MountPoint)
                        (eo v.FileSystem) (Fmt.bytes v.SizeBytes) (Fmt.bytes v.UsedBytes)
                        (capBar v.CapacityPercent) (if v.ReadOnly then "🔒 RO" else "RW"))
            let volMeta =
                [ sevChip Critical volCrit
                  sevChip Warning volWarn
                  (worstVolText |> Option.map (fun t -> chip "" (sprintf "worst: %s" t)))
                  plain (sprintf "%d mounted" (Stats.mountedCount vols)) ]
                |> List.choose id
            sectionTable "Volumes" "🗂️" vols.Length volMeta
                [ "Health", (if volCrit + volWarn > 0 then Some(sprintf "%d at risk" (volCrit + volWarn)) else Some "all healthy")
                  "Device", None
                  "Name", None
                  "Mount", Some(sprintf "%d mounted" (Stats.mountedCount vols))
                  "FS", None
                  // No total: APFS volumes share a container, so summing these
                  // columns would multiply one disk by its volume count.
                  "Size", None
                  "Used", None
                  "Capacity", (worstVolText |> Option.map (sprintf "worst %s"))
                  "Mode", None ]
                volRows

            // ---- USB ----
            if not inv.Usb.IsEmpty then
                let rows =
                    inv.Usb |> List.map (fun u ->
                        sprintf "<tr><td>🔌 %s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                            (esc u.Name) (eo u.Manufacturer) (eo u.Speed) (eo u.SerialNumber))
                let slowUsb =
                    inv.Usb
                    |> List.filter (fun u ->
                        match u.Speed with
                        | Some s -> Warnings.isSlowUsbSpeed s
                        | None -> false)
                    |> List.length
                let usbMeta = [ sevChip Warning slowUsb ] |> List.choose id
                sectionTable "USB Devices" "🔌" inv.Usb.Length usbMeta
                    [ "Name", None
                      "Manufacturer", None
                      "Speed", (if slowUsb > 0 then Some(sprintf "%d slow (USB 2.0)" slowUsb) else None)
                      "Serial", None ]
                    rows

            // ---- Thunderbolt ----
            if not inv.Thunderbolt.IsEmpty then
                let rows =
                    inv.Thunderbolt |> List.map (fun t ->
                        sprintf "<tr><td>⚡ %s</td><td>%s</td><td>%s</td></tr>"
                            (esc t.Name) (eo t.Vendor) (eo t.Speed))
                sectionTable "Thunderbolt Devices" "⚡" inv.Thunderbolt.Length []
                    [ "Name", None; "Vendor", None; "Speed", None ] rows

            // ---- NVMe ----
            if not inv.Nvme.IsEmpty then
                let rows =
                    inv.Nvme |> List.map (fun n ->
                        sprintf "<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                            (esc n.Name) (eo n.Model) (eo n.Capacity) (Fmt.boolYN n.TrimSupport) (eo n.SmartStatus))
                let trimOff =
                    inv.Nvme |> List.filter (fun n -> n.TrimSupport = Some false) |> List.length
                let nvmeMeta = [ sevChip Warning trimOff ] |> List.choose id
                sectionTable "NVMe Devices" "💽" inv.Nvme.Length nvmeMeta
                    [ "Name", None
                      "Model", None
                      "Capacity", None
                      "TRIM", (if trimOff > 0 then Some(sprintf "%d disabled" trimOff) else Some "enabled")
                      "SMART", None ]
                    rows

            // ---- Network mounts ----
            if not inv.NetworkMounts.IsEmpty then
                let rows =
                    inv.NetworkMounts |> List.map (fun n ->
                        sprintf "<tr><td>🌐 %s</td><td>%s</td><td>%s</td></tr>"
                            (esc n.Source) (esc n.MountPoint) (esc n.FsType))
                let fsTypes =
                    inv.NetworkMounts |> List.map (fun n -> n.FsType) |> List.distinct
                sectionTable "Network Mounts" "🌐" inv.NetworkMounts.Length
                    ([ plain (String.Join(", ", fsTypes)) ] |> List.choose id)
                    [ "Source", None; "Mount", None; "Type", Some(String.Join(", ", fsTypes)) ] rows

            // ---- Filesystem usage ----
            let fsRows =
                inv.FsUsage
                |> List.map (fun f ->
                    sprintf "<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                        (esc f.Filesystem) (esc f.MountPoint) (Fmt.bytes f.SizeBytes)
                        (Fmt.bytes f.UsedBytes) (Fmt.bytes f.AvailBytes) (capBar f.CapacityPercent))
            let worstFs = Stats.worstUsage inv.FsUsage
            let worstFsText =
                worstFs
                |> Option.map (fun f ->
                    sprintf "%s %d%%" f.MountPoint (f.CapacityPercent |> Option.defaultValue 0))
            // Verdict counts exclude pseudo-filesystems; the table below still
            // lists every row.
            let fsAtRisk =
                inv.FsUsage
                |> Stats.actionableUsage
                |> List.filter (fun f -> match f.CapacityPercent with Some p -> p >= 90 | None -> false)
                |> List.length
            let fsMeta =
                [ sevChip Critical fsAtRisk
                  (worstFsText |> Option.map (fun t -> chip "" (sprintf "worst: %s" t))) ]
                |> List.choose id
            sectionTable "Filesystem Usage" "📊" inv.FsUsage.Length fsMeta
                [ "Filesystem", None
                  "Mount", None
                  // Same container-sharing caveat as Volumes: no totals here.
                  "Size", None
                  "Used", None
                  "Avail", None
                  "Capacity", (worstFsText |> Option.map (sprintf "worst %s")) ]
                fsRows

            // ---- Raw JSON (collapsible) ----
            a "<details><summary>🧬 Raw Inventory (JSON)</summary>"
            a "<div class=\"tblwrap\" style=\"padding:1rem\"><pre style=\"white-space:pre-wrap;font-size:.75rem;margin:0\">"
            a (esc (Json.render inv))
            a "</pre></div></details>"

            a "</main>"
            a (sprintf "<footer>Generated by MacDiskExplorer.fsx · %s · no external dependencies</footer>" (esc (Fmt.iso inv.Timestamp)))

            // ---- JavaScript: theme, search, sort ----
            a jsBlock

            sb.ToString()

// ===================================================================
// Module: History
// Snapshot persistence and run-to-run diffing.
// ===================================================================
module History =
    open Model

    /// Archived instances retained per report type.
    let retentionCap = 50

    let historyDir (baseDir: string) = Path.Combine(baseDir, "history")

    /// Strip a trailing "-YYYYMMDD-HHmmss" so archived files can be grouped
    /// by report type ("disk-report-20260719-183949.html" -> "disk-report").
    let private stemOf (path: string) =
        let name = Path.GetFileNameWithoutExtension path
        Regex.Replace(name, @"-\d{8}-\d{6}$", "")

    /// Keep only the newest `keep` archived instances of each report type.
    /// Stamped names sort lexicographically in chronological order, so a
    /// descending sort puts the survivors first. Returns the deleted paths.
    let prune (keep: int) (baseDir: string) : string list =
        let dir = historyDir baseDir
        if keep <= 0 || not (Directory.Exists dir) then []
        else
            Directory.GetFiles dir
            |> Array.groupBy (fun f -> stemOf f, Path.GetExtension f)
            |> Array.collect (fun (_, files) ->
                files
                |> Array.sortDescending
                |> Array.skip (min keep files.Length))
            |> Array.filter (fun f ->
                try File.Delete f; true with _ -> false)
            |> Array.toList

    let save (baseDir: string) (inv: Inventory) : string =
        let dir = historyDir baseDir
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, sprintf "inventory-%s.json" (Fmt.stamp inv.Timestamp))
        File.WriteAllText(path, Render.Json.render inv)
        path

    let private latestPrevious (baseDir: string) : string option =
        let dir = historyDir baseDir
        if not (Directory.Exists dir) then None
        else
            Directory.GetFiles(dir, "inventory-*.json")
            |> Array.sort
            |> Array.tryLast

    /// Very small structural diff using the parsed JSON of the previous
    /// snapshot vs. the current inventory (device ids + sizes).
    let diffAgainstPrevious (baseDir: string) (inv: Inventory) : string list =
        match latestPrevious baseDir with
        | None -> [ "No previous snapshot — this is the first recorded run." ]
        | Some prevPath ->
            try
                let prevJson = File.ReadAllText prevPath
                match Jsonp.parse prevJson with
                | None -> [ "Previous snapshot could not be parsed." ]
                | Some root ->
                    let prevDisks =
                        Jsonp.prop "disks" root
                        |> Option.map Jsonp.items
                        |> Option.defaultValue []
                        |> List.choose (fun d ->
                            match Jsonp.strProp "deviceId" d with
                            | Some id -> Some(id, Jsonp.strProp "sizeBytes" d)
                            | None -> None)
                        |> Map.ofList
                    let curDisks =
                        inv.Disks
                        |> List.map (fun d -> d.DeviceId, (d.SizeBytes |> Option.map string))
                        |> Map.ofList
                    [ for KeyValue(id, _) in curDisks do
                        if not (prevDisks.ContainsKey id) then
                            yield sprintf "🟢 New disk since last run: %s" id
                      for KeyValue(id, _) in prevDisks do
                        if not (curDisks.ContainsKey id) then
                            yield sprintf "🔴 Removed disk since last run: %s" id
                      for KeyValue(id, curSize) in curDisks do
                        match prevDisks.TryFind id with
                        | Some prevSize when prevSize <> curSize ->
                            yield sprintf "🟡 Capacity change on %s: %s → %s"
                                      id (Fmt.str prevSize) (Fmt.str curSize)
                        | _ -> () ]
                    |> function
                        | [] -> [ "No structural changes since last snapshot." ]
                        | xs -> xs
            with ex -> [ sprintf "Diff failed: %s" ex.Message ]

// ===================================================================
// Module: Cli
// Argument parsing, output dispatch, watch loop.
// ===================================================================
module Cli =
    open Model

    type Options =
        { Html: bool
          Json: bool
          Csv: bool
          Markdown: bool
          Console: bool
          Watch: bool
          Help: bool
          OutDir: string }

    let private empty =
        { Html = false; Json = false; Csv = false; Markdown = false
          Console = false; Watch = false; Help = false; OutDir = "." }

    /// Recursive parse so `--out <dir>` can consume its argument. Flags are
    /// matched by their bare name after stripping leading dashes, so single-
    /// (`-html`) and double-dash (`--html`) forms are both accepted, and any
    /// unrecognized token is reported rather than silently ignored.
    let parse (argv: string list) : Options =
        let rec loop acc args =
            match args with
            | [] -> acc
            | (a: string) :: rest ->
                match a.TrimStart('-').ToLowerInvariant() with
                | "html" -> loop { acc with Html = true } rest
                | "json" -> loop { acc with Json = true } rest
                | "csv" -> loop { acc with Csv = true } rest
                | "markdown" | "md" -> loop { acc with Markdown = true } rest
                | "console" -> loop { acc with Console = true } rest
                | "watch" -> loop { acc with Watch = true } rest
                | "all" ->
                    loop { acc with Html = true; Json = true; Csv = true; Markdown = true; Console = true } rest
                | "help" | "h" | "?" -> loop { acc with Help = true } rest
                | "out" ->
                    match rest with
                    | dir :: more -> loop { acc with OutDir = dir } more
                    | [] ->
                        eprintfn "⚠️  --out requires a directory argument; ignoring it."
                        loop acc []
                | _ ->
                    eprintfn "⚠️  Ignoring unrecognized option: %s (run with 'help' for usage)" a
                    loop acc rest
        let o = loop empty argv
        // Default to console if nothing else was requested.
        if not (o.Html || o.Json || o.Csv || o.Markdown || o.Console || o.Help) then
            { o with Console = true }
        else o

    let printHelp () =
        printfn """
Mac Disk Explorer — macOS storage inventory & diagnostics

USAGE:
    dotnet fsi MacDiskExplorer.fsx [options]

OPTIONS:
    --console     Print an ASCII report to the terminal (default)
    --html        Write disk-report.html (self-contained dashboard)
    --json        Write inventory.json (full structured inventory)
    --csv         Write disks.csv (flattened disk/volume view)
    --markdown    Write report.md (readable report)
    --all         Produce every output above

    The output directory holds only the latest, unstamped copy of each report,
    so a bookmark or open tab always points at the current run. Every run is
    also archived to history/ as <name>-YYYYMMDD-HHmmss.<ext>, keeping the 50
    most recent instances of each report type.
    --out <dir>   Write outputs and history snapshots under <dir> (default: .)
    --watch       Refresh the console report periodically (Ctrl+C to stop)
    --help        Show this help

    Note: because `dotnet fsi` intercepts a bare `--help`, use the bare word
    `help` or run `dotnet fsi MacDiskExplorer.fsx -- --help`.

EXAMPLES:
    dotnet fsi MacDiskExplorer.fsx --all
    dotnet fsi MacDiskExplorer.fsx --html --json --out reports
    dotnet fsi MacDiskExplorer.fsx --console --watch
"""

    /// Insert the run's timestamp before the extension:
    /// "disk-report.html" -> "disk-report-20260719-183045.html".
    ///
    /// Every file from a run shares the collection timestamp rather than each
    /// taking its own write time, so a run's outputs sort together and the
    /// stamp matches the "Generated" time printed inside the report.
    let stampName (timestamp: DateTime) (name: string) =
        let ext = Path.GetExtension name
        let stem = Path.GetFileNameWithoutExtension name
        sprintf "%s-%s%s" stem (Fmt.stamp timestamp) ext

    /// Produce all requested file outputs and report where they landed.
    ///
    /// The main folder holds only the unstamped "latest" of each report, so a
    /// bookmark or open browser tab always points at the current run. Every
    /// run is additionally archived under history/ with a timestamped name.
    let private writeOutputs (o: Options) (inv: Inventory) =
        if o.Console then
            printfn "%s" (Render.Console.render inv)

        let archiveDir = History.historyDir o.OutDir

        /// `archive` is false for the JSON inventory, whose stamped copy is
        /// written by History.save (it is needed for diffing even when --json
        /// was not requested), so it is not written twice.
        let write name (archive: bool) (content: string) =
            if o.OutDir <> "." then Directory.CreateDirectory o.OutDir |> ignore
            let latest = Path.Combine(o.OutDir, name)
            File.WriteAllText(latest, content)
            if archive then
                Directory.CreateDirectory archiveDir |> ignore
                File.WriteAllText(Path.Combine(archiveDir, stampName inv.Timestamp name), content)
            printfn "📄 Wrote %s" latest

        if o.Html then write "disk-report.html" true (Render.Html.render inv)
        if o.Json then write "inventory.json" false (Render.Json.render inv)
        if o.Csv then write "disks.csv" true (Render.Csv.render inv)
        if o.Markdown then write "report.md" true (Render.Markdown.render inv)

    let run (o: Options) =
        if o.Help then
            printHelp ()
        elif o.Watch then
            printfn "👁  Watch mode — refreshing every 5s. Press Ctrl+C to stop.\n"
            let mutable running = true
            Console.CancelKeyPress.Add(fun args ->
                args.Cancel <- true
                running <- false
                printfn "\n👋 Stopping watch mode.")
            while running do
                Console.Clear()
                let inv = Engine.collect ()
                printfn "%s" (Render.Console.render inv)
                printfn "\n(refreshing in 5s — Ctrl+C to stop)"
                // Sleep in short slices so Ctrl+C is responsive.
                let mutable slept = 0
                while running && slept < 5000 do
                    Thread.Sleep 250
                    slept <- slept + 250
        else
            printfn "🔎 Collecting macOS storage inventory…"
            let inv = Engine.collect ()

            // History snapshot + diff (diff must read the previous snapshot
            // before we write the new one).
            let diffs = History.diffAgainstPrevious o.OutDir inv
            let snapPath = History.save o.OutDir inv

            writeOutputs o inv

            // Trim the archive last, so this run's files are already in place
            // and counted toward the retention window.
            let pruned = History.prune History.retentionCap o.OutDir

            printfn "\n🕓 History:"
            diffs |> List.iter (fun d -> printfn "   %s" d)
            printfn "   💾 Archived to %s" snapPath
            if not pruned.IsEmpty then
                printfn "   🧹 Pruned %d file(s) beyond the %d-per-type cap"
                    pruned.Length History.retentionCap

// ===================================================================
// Entry point
//
// Only run when this file is the script fsi was invoked with. When it is
// loaded via `#load` (e.g. from the test file), the entry script differs,
// so collection does not run and the modules can be exercised directly.
// ===================================================================
let private invokedScript =
    fsi.CommandLineArgs
    |> Array.tryHead
    |> Option.map Path.GetFileName
    |> Option.defaultValue ""

if invokedScript.Equals("MacDiskExplorer.fsx", StringComparison.OrdinalIgnoreCase) then
    let argv = fsi.CommandLineArgs |> Array.toList |> List.tail  // drop script path
    Cli.run (Cli.parse argv)
