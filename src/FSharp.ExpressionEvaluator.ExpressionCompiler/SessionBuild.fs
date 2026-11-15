/// What an evaluation gets from the session build of its AppDomain (docs/DESIGN.md §6.9).
module internal FSharp.ExpressionEvaluator.SessionBuild

open System
open System.Threading.Tasks

type SessionState<'T> =
    | Ready of 'T
    /// Still being built after the time the evaluation waits.
    | Building
    /// The build failed. It is kept for the modules it was for: a new build of the same modules
    /// would fail the same way, and it takes long, with the debugger waiting.
    | Failed

/// The state of a session build after a wait of at most `timeout`. A failed build gives Failed at
/// once, without its exception: the build logged it.
let state (build: Task<'T>) (timeout: TimeSpan) =
    let ended =
        try
            build.Wait timeout
        with :? AggregateException ->
            true

    if not ended then Building
    elif build.Status = TaskStatus.RanToCompletion then Ready build.Result
    else Failed
