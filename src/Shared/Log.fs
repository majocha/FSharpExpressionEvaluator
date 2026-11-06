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

let private tracing = lazy (Environment.GetEnvironmentVariable "FSHARP_EE_TRACE" = "1")

let mutable private nextCall = 0

/// An asynchronous call traced with FSHARP_EE_TRACE=1: its start, its completion, and whether it was
/// still pending when the method that started it returned (on Dispose), each with the thread it ran on.
type TracedCall(source: string, what: string) =
    let id = if tracing.Value then Threading.Interlocked.Increment(&nextCall) else 0
    let mutable completed = false
    let thread () = Threading.Thread.CurrentThread.ManagedThreadId

    do
        if tracing.Value then
            write source $"#{id} {what} (thread {thread ()})"

    member _.Completed() =
        completed <- true

        if tracing.Value then
            write source $"#{id} completed (thread {thread ()})"

    interface IDisposable with
        member _.Dispose() =
            if tracing.Value && not completed then
                write source $"#{id} returned, pending (thread {thread ()})"

/// Starts tracing a call (docs/DESIGN.md §17): to find a call whose completion routine never runs.
let trace (source: string) (what: string) = new TracedCall(source, what)
