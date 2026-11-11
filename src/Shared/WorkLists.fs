/// Calls to other Concord components, run to completion on a work list of our own (linked into each
/// component assembly; docs/DESIGN.md §17, item 19).
///
/// A call on the caller's work list returns pending: Concord runs it as a later item of that list. When
/// a window abandons its request (the Locals window cutting a refresh short), those items never run,
/// their completion routines never run, and VS waits for them forever before it continues the
/// debuggee. A call run here completes before the method that made it returns.
module internal FSharp.ExpressionEvaluator.WorkLists

open Microsoft.VisualStudio.Debugger

/// Runs `start` on a work list of our own, to completion, and returns its result; None when that
/// work list can't run here (logged). `what` names the call in the log; it runs only on a failure.
let runToCompletion (source: string) (what: unit -> string) (start: DkmWorkList -> DkmCompletionRoutine<'T> -> unit) : 'T option =
    let workList = DkmWorkList.Create(null)
    let result = ref None

    try
        start workList (DkmCompletionRoutine<'T>(fun r -> result.Value <- Some r))
        workList.Execute()
        result.Value
    with e ->
        Log.exn source $"{what ()} on our own work list" e

        try
            workList.Cancel()
        with _ ->
            ()

        None

/// Runs `start` to completion and passes its result to `k`, before returning. When our own work list
/// can't run, `start` runs on the caller's work list as before.
let complete (source: string) (what: unit -> string) (workList: DkmWorkList) (start: DkmWorkList -> DkmCompletionRoutine<'T> -> unit) (k: 'T -> unit) =
    match runToCompletion source what start with
    | Some r -> k r
    | None -> start workList (DkmCompletionRoutine<'T>(k))

/// Starts tracing a call (Log.trace) and wraps its completion routine to log the completion. Dispose
/// the call when the method that started it returns. With tracing off (the usual case) it gives a
/// shared call that does nothing and the completion routine itself, and `what` does not run.
let inline traced (source: string) ([<InlineIfLambda>] what: unit -> string) (completionRoutine: DkmCompletionRoutine<'T>) =
    if not Log.isTracing.Value then
        struct (Log.noCall, completionRoutine)
    else
        let call = Log.trace source (what ())

        struct (call :> System.IDisposable,
                DkmCompletionRoutine<'T>(fun r ->
                    call.Completed()
                    completionRoutine.Invoke r))

/// The same inspection context with more evaluation flags.
let withEvaluationFlags (flags: Evaluation.DkmEvaluationFlags) (c: Evaluation.DkmInspectionContext) =
    Evaluation.DkmInspectionContext.Create(
        c.InspectionSession,
        c.RuntimeInstance,
        c.Thread,
        c.Timeout,
        c.EvaluationFlags ||| flags,
        c.FuncEvalFlags,
        c.Radix,
        c.Language,
        c.ReturnValue,
        c.AdditionalVisualizationData,
        c.AdditionalVisualizationDataPriority,
        c.ReturnValues,
        c.SymbolsConnection
    )
