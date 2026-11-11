/// Diagnostic log shared by all EE components (linked into each assembly).
/// Writes to %FSHARP_EE_LOG% if set, otherwise %LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log.
/// By default it gets errors, load lines and timings only. With FSHARP_EE_LOG_VERBOSE=1 it also gets
/// lines that can contain user data: frame names with argument values, expression texts, module names.
module internal FSharp.ExpressionEvaluator.Log

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

let private path =
    match Environment.GetEnvironmentVariable "FSHARP_EE_LOG" with
    | null
    | "" ->
        let dir =
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "FSharpExpressionEvaluator")

        Path.Combine(dir, "ee.log")
    | p -> p

/// The size at which the first line of a process moves the log to `<log>.1` (and replaces the
/// `<log>.1` of before), so the two files together stay below twice this size.
let maxSize = 4L * 1024L * 1024L

/// Moves `file` to `file.1` when it is larger than `limit`. Returns whether it moved the file. The
/// caller holds the lock of the file (`withFileLock`).
let rollOver (file: string) (limit: int64) =
    let info = FileInfo file

    if info.Exists && info.Length > limit then
        let previous = file + ".1"
        File.Delete previous
        File.Move(file, previous)
        true
    else
        false

/// Appends `text` to `file`. Other processes can read and write the file at the same time; the
/// caller holds the lock of the file, so lines of different processes do not mix.
let append (file: string) (text: string) =
    use stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
    let bytes = Encoding.UTF8.GetBytes text
    stream.Write(bytes, 0, bytes.Length)

/// Runs `f` with a lock on `file` that all processes of the user's session share (devenv, msvsmon,
/// several devenv instances). Without the lock (it cannot be created, or another process holds it
/// for more than a second) `f` runs all the same: a line can then mix with another one, but it is
/// not lost.
let withFileLock (file: string) (f: unit -> 'T) =
    // The same name in every process: string.GetHashCode differs between processes on .NET Core.
    let hash =
        (Path.GetFullPath file).ToUpperInvariant()
        |> Seq.fold (fun (h: uint32) c -> (h ^^^ uint32 c) * 16777619u) 2166136261u

    let name = "Local\\FSharpExpressionEvaluator.Log." + hash.ToString "x8"

    let mutex =
        try
            Some(new Mutex(false, name))
        with _ ->
            None

    match mutex with
    | None -> f ()
    | Some mutex ->
        use _ = mutex

        let owned =
            try
                mutex.WaitOne(TimeSpan.FromSeconds 1.0)
            with :? AbandonedMutexException ->
                // The process that held it ended; the lock is now ours.
                true

        try
            f ()
        finally
            if owned then
                mutex.ReleaseMutex()

let private gate = obj ()
let mutable private checkedSize = false

let private processTag =
    lazy
        (let p = Process.GetCurrentProcess()
         let bits = if Environment.Is64BitProcess then "x64" else "x86"
         $"{p.ProcessName}:{p.Id}:{bits}")

let write (source: string) (message: string) =
    try
        let line = $"{DateTime.Now:``HH:mm:ss.fff``} [{processTag.Value}] [{source}] {message}{Environment.NewLine}"

        lock gate (fun () ->
            withFileLock path (fun () ->
                if not checkedSize then
                    checkedSize <- true
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

                    try
                        rollOver path maxSize |> ignore
                    with _ ->
                        // Another process has the file open without delete sharing: it stays as it is.
                        ()

                append path line))
    with _ ->
        ()

let exn (source: string) (context: string) (e: exn) = write source $"{context} failed: {e}"

/// With FSHARP_EE_LOG_VERBOSE=1 the components also log lines that can contain user data or that
/// come for each frame or row: frame names (with argument values when the debugger shows them),
/// expression texts, module names (eng\Probe-Debugger.ps1's -ExpectLog reads frame names).
let isVerbose = lazy (Environment.GetEnvironmentVariable "FSHARP_EE_LOG_VERBOSE" = "1")

/// Writes the line that `message` makes, only with FSHARP_EE_LOG_VERBOSE=1. Without it, `message`
/// does not run.
let verbose (source: string) (message: unit -> string) =
    if isVerbose.Value then
        try
            write source (message ())
        with _ ->
            ()

/// With FSHARP_EE_LOG_FULL_NAMES=1 the result provider logs the full name of each row it lists, the
/// expression that Add Watch and edits use (eng\Probe-Debugger.ps1's -ExpectFullName reads them).
let fullNames = lazy (Environment.GetEnvironmentVariable "FSHARP_EE_LOG_FULL_NAMES" = "1")

/// Whether FSHARP_EE_TRACE=1 asks to trace asynchronous calls (`trace`).
let isTracing = lazy (Environment.GetEnvironmentVariable "FSHARP_EE_TRACE" = "1")

let private tracing = isTracing

/// A traced call that does nothing, for when tracing is off.
let noCall =
    { new IDisposable with
        member _.Dispose() = () }

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
