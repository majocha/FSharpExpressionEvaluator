/// Minimal PE parsing over a random-access reader, for images in debuggee memory where only
/// small ranges can be read (docs/DESIGN.md §6.2). No dependency on Concord.
module internal FSharp.ExpressionEvaluator.PeImage

open System

/// Reads `count` bytes at `offset` from the start of the image (file offset or RVA, per layout).
type ReadImage = int -> int -> byte[]

type CliResources =
    {
        /// RVA of the CLI header's managed resources directory.
        ResourcesRva: int
        /// Maps an RVA to an offset readable through ReadImage.
        ToImageOffset: int -> int
    }

let private u16 (b: byte[]) i = int (BitConverter.ToUInt16(b, i))
let private i32 (b: byte[]) i = BitConverter.ToInt32(b, i)

/// Locates the managed resources directory. `isLoadedImage` means the loader's mapped layout,
/// where RVA = offset; otherwise RVAs are mapped to file offsets through the section table.
let cliResources (read: ReadImage) (isLoadedImage: bool) : CliResources =
    let dos = read 0 0x40
    if u16 dos 0 <> 0x5A4D then failwith "missing MZ signature"
    let peOffset = i32 dos 0x3C
    let coff = read peOffset 24
    if i32 coff 0 <> 0x4550 then failwith "missing PE signature"
    let sectionCount = u16 coff 6
    let optionalHeaderSize = u16 coff 20
    let optional = read (peOffset + 24) optionalHeaderSize

    let dataDirectories =
        match u16 optional 0 with
        | 0x10B -> 96
        | 0x20B -> 112
        | magic -> failwith $"unknown optional header magic 0x{magic:X}"

    // Data directory 14 is the CLI header.
    let cliHeaderRva = i32 optional (dataDirectories + 14 * 8)

    let sections =
        let table = read (peOffset + 24 + optionalHeaderSize) (sectionCount * 40)

        [ for s in 0 .. sectionCount - 1 ->
              let at = s * 40
              {| VirtualAddress = i32 table (at + 12); VirtualSize = i32 table (at + 8); PointerToRawData = i32 table (at + 20) |} ]

    let toImageOffset rva =
        if isLoadedImage then
            rva
        else
            match sections |> List.tryFind (fun s -> s.VirtualAddress <= rva && rva < s.VirtualAddress + s.VirtualSize) with
            | Some s -> rva - s.VirtualAddress + s.PointerToRawData
            | None -> failwith $"RVA 0x{rva:X} is outside every section"

    if cliHeaderRva = 0 then failwith "not a managed image"
    let cliHeader = read (toImageOffset cliHeaderRva) 72
    // CLI header: cb, runtime versions, MetaData (8), Flags, EntryPoint, then Resources at 24.
    { ResourcesRva = i32 cliHeader 24; ToImageOffset = toImageOffset }

/// Reads one embedded managed resource (4-byte length prefix, then data) at its manifest offset.
let readResource (read: ReadImage) (resources: CliResources) (manifestOffset: int) =
    let start = resources.ToImageOffset(resources.ResourcesRva + manifestOffset)
    let length = BitConverter.ToInt32(read start 4, 0)
    read (start + 4) length
