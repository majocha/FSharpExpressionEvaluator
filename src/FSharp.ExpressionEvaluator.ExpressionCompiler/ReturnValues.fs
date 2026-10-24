namespace FSharp.ExpressionEvaluator

open System
open System.Collections.ObjectModel
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation

/// Return values listed in F# frames after a step ("f returned", docs/DESIGN.md §6.5). They are the
/// `$ReturnValueN` aliases of the locals query, named by the debugger after the method that
/// returned them (a C#-style name: the debugger gives no method token).
module ReturnValues =

    let private log = Log.write "RV"

    let private isReturnValue (local: DkmClrLocalVariableInfo) =
        not (isNull local.FullName)
        && local.FullName.StartsWith("$ReturnValue", StringComparison.Ordinal)

    /// FSharp.Core's types are all in `Microsoft.FSharp.*`.
    let private isFromFSharpCore (local: DkmClrLocalVariableInfo) =
        not (isNull local.VariableName)
        && local.VariableName.StartsWith("Microsoft.FSharp.", StringComparison.Ordinal)

    /// Leaves out return values of FSharp.Core methods: they are mostly calls the compiler emits for
    /// F# constructs, such as a list literal's `Cons` and `Empty`.
    let filterLocals (query: DkmCompiledClrLocalsQuery) =
        if isNull query then
            query
        else
            let kept =
                query.LocalInfo
                |> Seq.filter (fun local -> not (isReturnValue local && isFromFSharpCore local))
                |> ResizeArray

            if kept.Count = query.LocalInfo.Count then
                query
            else
                log $"left out {query.LocalInfo.Count - kept.Count} FSharp.Core return value(s)"

                DkmCompiledClrLocalsQuery.Create(
                    query.RuntimeInstance,
                    query.DataContainer,
                    query.LanguageId,
                    query.Binary,
                    query.TypeName,
                    ReadOnlyCollection kept
                )
