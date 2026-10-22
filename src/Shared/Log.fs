/// Diagnostic log shared by all EE components (linked into each assembly).
/// Writes to %FSHARP_EE_LOG% if set, otherwise %LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log.
module internal FSharp.ExpressionEvaluator.Log

open System
open System.Diagnostics
open System.IO

let private path =
    match Environment.GetEnvironmentVariable "FSHARP_EE_LOG" with
    | null
    | "" ->
        let dir =
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "FSharpExpressionEvaluator")

        Path.Combine(dir, "ee.log")
    | p -> p

let private gate = obj ()

let private processTag =
    lazy
        (let p = Process.GetCurrentProcess()
         let bits = if Environment.Is64BitProcess then "x64" else "x86"
         $"{p.ProcessName}:{p.Id}:{bits}")

let write (source: string) (message: string) =
    try
        lock gate (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let line = $"{DateTime.Now:``HH:mm:ss.fff``} [{processTag.Value}] [{source}] {message}{Environment.NewLine}"
            File.AppendAllText(path, line))
    with _ ->
        ()

let exn (source: string) (context: string) (e: exn) = write source $"{context} failed: {e}"
