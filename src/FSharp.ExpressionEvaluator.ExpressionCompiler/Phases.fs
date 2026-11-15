/// The times of the phases of a session build or of a compilation, for the log (docs/DESIGN.md
/// §6.9). A caller records the phases that run on its thread; without a caller that records, a
/// phase costs one thread-local read.
/// A task that the recording thread waits for can run inline on that thread (`Task.Wait`,
/// `Task.Result`, `Async.RunSynchronouslyImmediate`). Its phases are then recorded too, for
/// example the "PE images" of an import inside `Create`. They are part of the time that the
/// recorded code took, so the times stay correct.
module internal FSharp.ExpressionEvaluator.Phases

open System.Diagnostics
open System.Threading

let private current = new ThreadLocal<ResizeArray<string * float>>()

/// Runs `f` and adds its time to the phase `name` of the recording on this thread, if there is one.
let time (name: string) (f: unit -> 'T) =
    match current.Value with
    | null -> f ()
    | phases ->
        let sw = Stopwatch.StartNew()

        try
            f ()
        finally
            phases.Add(name, sw.Elapsed.TotalMilliseconds)

/// Runs `f` and gives its result with its time and the times of the phases that ran in it, on this
/// thread: `<total> ms (<name> <ms>, ...)`, each phase name once, in the order they first ran, with
/// the sum of its runs. A phase in another phase is also in the time of that phase.
let record (f: unit -> 'T) =
    let outer = current.Value
    let phases = ResizeArray()
    current.Value <- phases
    let sw = Stopwatch.StartNew()

    let value =
        try
            f ()
        finally
            current.Value <- outer

    let text =
        phases
        |> Seq.groupBy fst
        |> Seq.map (fun (name, runs) -> $"{name} {runs |> Seq.sumBy snd |> round |> int}")
        |> String.concat ", "

    value, $"{sw.ElapsedMilliseconds} ms ({text})"
