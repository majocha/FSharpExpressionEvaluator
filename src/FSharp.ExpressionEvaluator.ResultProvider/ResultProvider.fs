namespace FSharp.ExpressionEvaluator

open System
open System.Collections.ObjectModel
open System.Reflection
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation

/// Attached to results this component created, so later GetChildren/GetClrValue calls
/// can tell them apart from results created by the C# result provider.
[<AllowNullLiteral>]
type internal FSharpResultData(value: DkmClrValue, fullName: string) =
    inherit DkmDataItem()
    member _.Value = value
    member _.FullName = fullName

[<AllowNullLiteral>]
type internal ChildrenData(children: DkmEvaluationResult[]) =
    inherit DkmDataItem()
    member _.Children = children

/// M0 spike (docs/DESIGN.md §5.1): renders F# lists itself and chains everything else
/// to the next IDkmClrResultProvider (Roslyn's C# one), logging which path was taken.
type ResultProvider() =

    static let log = Log.write "RP"
    static do log "ResultProvider loaded"

    static let noFormatSpecifiers = ReadOnlyCollection<string>([||])

    static let maxItems = 100

    static let field (ctx: DkmInspectionContext) name (value: DkmClrValue) =
        value.GetMemberValue(name, int MemberTypes.Field, null, ctx)

    static let isError (value: DkmClrValue) =
        value.ValueFlags.HasFlag DkmClrValueFlags.Error

    /// Items of an F# list, read from its fields (no func-eval).
    static let listItems ctx (list: DkmClrValue) =
        let rec loop (node: DkmClrValue) acc n =
            let tail = field ctx "tail" node

            if isError tail || tail.IsNull || n = maxItems then
                List.rev acc, not tail.IsNull
            else
                loop tail (field ctx "head" node :: acc) (n + 1)

        loop list [] 0

    static let formatList ctx (items: DkmClrValue list) truncated =
        let shown = items |> List.truncate 10 |> List.map (fun v -> v.GetValueString(ctx, noFormatSpecifiers))
        let more = if truncated || items.Length > 10 then "; ..." else ""
        "[" + String.Join("; ", shown) + more + "]"

    static let tryCreateFSharpResult (value: DkmClrValue) ctx name fullName =
        if isNull value || value.IsNull || isError value then
            None
        else
            let lmrType = value.Type.GetLmrType()

            if FSharpShapes.isFSharpList lmrType then
                let items, truncated = listItems ctx value

                let flags =
                    DkmEvaluationResultFlags.ReadOnly
                    ||| (if items.IsEmpty then DkmEvaluationResultFlags.None else DkmEvaluationResultFlags.Expandable)

                DkmSuccessEvaluationResult.Create(
                    ctx,
                    value.StackFrame,
                    name,
                    fullName,
                    flags,
                    formatList ctx items truncated,
                    null,
                    FSharpShapes.typeName lmrType,
                    DkmEvaluationResultCategory.Data,
                    DkmEvaluationResultAccessType.None,
                    DkmEvaluationResultStorageType.None,
                    DkmEvaluationResultTypeModifierFlags.None,
                    value.Address,
                    null,
                    null,
                    FSharpResultData(value, fullName)
                )
                |> Some
            else
                None

    /// Gets results for several values one after another through Concord, then calls k.
    static let getResultsSequentially
        (workList: DkmWorkList)
        ctx
        (requests: (DkmClrValue * DkmClrType * string * string)[])
        (k: DkmEvaluationResult[] -> unit)
        =
        let results = Array.zeroCreate requests.Length

        let rec next i =
            if i = requests.Length then
                k results
            else
                let value, declaredType, name, fullName = requests[i]

                let complete (r: DkmEvaluationAsyncResult) =
                    results[i] <-
                        match r.Result with
                        | null ->
                            DkmFailedEvaluationResult.Create(ctx, value.StackFrame, name, fullName, $"error 0x{r.ErrorCode:X8}", DkmEvaluationResultFlags.None, null)
                            :> DkmEvaluationResult
                        | result -> result

                    next (i + 1)

                // Chains to the next result provider (C#); our own GetResult is not re-entered.
                value.GetResult(workList, declaredType, null, ctx, noFormatSpecifiers, name, fullName, DkmCompletionRoutine<_>(complete))

        next 0

    interface IDkmClrResultProvider with

        member _.GetResult
            (clrValue, workList, declaredType, customTypeInfo, inspectionContext, formatSpecifiers, resultName, resultFullName, completionRoutine)
            =
            let created =
                try
                    tryCreateFSharpResult clrValue inspectionContext resultName resultFullName
                with e ->
                    Log.exn "RP" $"GetResult '{resultName}'" e
                    None

            match created with
            | Some result ->
                log $"GetResult '{resultName}' -> F# result"
                completionRoutine.Invoke(DkmEvaluationAsyncResult(result))
            | None ->
                log $"GetResult '{resultName}' ({clrValue.Type.GetLmrType().FullName}) -> chained"

                clrValue.GetResult(
                    workList,
                    declaredType,
                    customTypeInfo,
                    inspectionContext,
                    formatSpecifiers,
                    resultName,
                    resultFullName,
                    completionRoutine
                )

        member _.GetChildren(result, workList, initialRequestSize, inspectionContext, completionRoutine) =
            match result.GetDataItem<FSharpResultData>() with
            | null ->
                log $"GetChildren '{result.Name}' -> chained"
                result.GetChildren(workList, initialRequestSize, inspectionContext, completionRoutine)
            | data ->
                log $"GetChildren '{result.Name}' -> F# list items"
                let items, _ = listItems inspectionContext data.Value
                let itemType = data.Value.Type.GenericArguments[0]

                let requests =
                    items
                    |> List.mapi (fun i item -> item, itemType, $"[{i}]", $"{data.FullName}[{i}]")
                    |> List.toArray

                getResultsSequentially workList inspectionContext requests (fun children ->
                    let initial = Array.truncate initialRequestSize children

                    let enumContext =
                        DkmEvaluationResultEnumContext.Create(children.Length, result.StackFrame, inspectionContext, ChildrenData children)

                    completionRoutine.Invoke(DkmGetChildrenAsyncResult(initial, enumContext)))

        member _.GetItems(enumContext, workList, startIndex, count, completionRoutine) =
            match enumContext.GetDataItem<ChildrenData>() with
            | null -> enumContext.GetItems(workList, startIndex, count, completionRoutine)
            | data ->
                let items = data.Children |> Array.skip (min startIndex data.Children.Length) |> Array.truncate count
                completionRoutine.Invoke(DkmEvaluationEnumAsyncResult(items))

        member _.GetClrValue(successResult) =
            match successResult.GetDataItem<FSharpResultData>() with
            | null -> successResult.GetClrValue()
            | data -> data.Value

        member _.GetUnderlyingString(result) = result.GetUnderlyingString()
