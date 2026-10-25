namespace FSharp.ExpressionEvaluator

open System.Collections.ObjectModel
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open FSharp.ExpressionEvaluator.FSharpValues

/// A row under an F# value: one of its F# children, or the Raw View delegated to C#.
type internal Row =
    | ChildRow of Child
    | RawViewRow

/// Attached to results this component created, so later calls can tell them from C# results.
[<AllowNullLiteral>]
type internal FSharpResultData(clrValue: DkmClrValue, value: IValue, declaredType: DkmClrType, fullName: string) =
    inherit DkmDataItem()
    member _.ClrValue = clrValue
    member _.Value = value
    member _.DeclaredType = declaredType
    member _.FullName = fullName

/// The Raw View row: shown without a value (so no func-eval display), its children come from the
/// C# provider with the `raw` specifier.
[<AllowNullLiteral>]
type internal RawViewData(parent: FSharpResultData) =
    inherit DkmDataItem()
    member _.Parent = parent

/// Attached to a copy of a C# result that only changes its Type column to F# names; children,
/// the CLR value and the underlying string come from the C# original.
[<AllowNullLiteral>]
type internal RetypedData(original: DkmEvaluationResult) =
    inherit DkmDataItem()
    member _.Original = original

[<AllowNullLiteral>]
type internal RowsData(parent: FSharpResultData, rows: Row[]) =
    inherit DkmDataItem()
    member _.Parent = parent
    member _.Rows = rows

/// F# result provider (docs/DESIGN.md §5.1): renders F# shapes from memory and chains every other
/// value to the next IDkmClrResultProvider (Roslyn's C# one). Children of F# values are created
/// through Concord again, so each lands with whichever provider owns its shape. While the F# EE is
/// switched off (Settings.fs), new values all go to C#; results created before still expand as F#.
type ResultProvider() =

    static let log = Log.write "RP"
    static do log "ResultProvider loaded"

    static let options = FSharpValues.defaultOptions
    static let noFormatSpecifiers = ReadOnlyCollection<string>([||])

    static let hasSpecifier (name: string) (specifiers: ReadOnlyCollection<string>) =
        not (isNull specifiers) && specifiers.Contains name

    static let lmrType (t: DkmClrType) =
        if isNull t then None else Lmr.safe None (fun () -> Some(LmrType.Of(t.GetLmrType())))

    static let typeColumn (declared: IType option) (value: IValue) =
        match declared with
        | Some d when not value.IsNull && FSharpTypes.shape d = FSharpTypes.NotFSharp ->
            // e.g. a list held in an `obj`: show both, as the C# provider does.
            $"{FSharpTypes.typeName d} {{{FSharpTypes.typeName (FSharpTypes.displayType value.Type)}}}"
        | _ -> FSharpValues.typeNameOf declared value

    static let tryCreate (clrValue: DkmClrValue) (declaredType: DkmClrType) context name fullName =
        let value = ConcordValue(clrValue, context) :> IValue
        let declared = lmrType declaredType

        FSharpValues.tryPreview options declared value
        |> Option.map (fun preview ->
            let flags =
                DkmEvaluationResultFlags.ReadOnly
                ||| (if FSharpValues.hasChildren value then DkmEvaluationResultFlags.Expandable else DkmEvaluationResultFlags.None)

            DkmSuccessEvaluationResult.Create(
                context,
                clrValue.StackFrame,
                name,
                fullName,
                flags,
                preview,
                null,
                typeColumn declared value,
                DkmEvaluationResultCategory.Data,
                DkmEvaluationResultAccessType.None,
                DkmEvaluationResultStorageType.None,
                DkmEvaluationResultTypeModifierFlags.None,
                clrValue.Address,
                null,
                null,
                FSharpResultData(clrValue, value, declaredType, fullName)
            ))

    /// C#'s result for a value we do not render, with F# names in the Type column when its type
    /// mentions FSharp.Core (`System.Threading.Tasks.Task<unit>`).
    static let retype (clrValue: DkmClrValue) (declaredType: DkmClrType) context (result: DkmEvaluationResult) =
        match result with
        | :? DkmSuccessEvaluationResult as s ->
            let value = ConcordValue(clrValue, context) :> IValue

            match FSharpValues.csharpTypeColumn (lmrType declaredType) value with
            | Some typeName when typeName <> s.Type ->
                DkmSuccessEvaluationResult.Create(
                    s.InspectionContext,
                    s.StackFrame,
                    s.Name,
                    s.FullName,
                    s.Flags,
                    s.Value,
                    s.EditableValue,
                    typeName,
                    s.Category,
                    s.Access,
                    s.StorageType,
                    s.TypeModifierFlags,
                    s.Address,
                    s.CustomUIVisualizers,
                    s.ExternalModules,
                    RetypedData s
                )
                :> DkmEvaluationResult
            | _ -> result
        | _ -> result

    static let rows (data: FSharpResultData) =
        let fullName = Option.ofObj data.FullName
        let children = FSharpValues.tryChildren options fullName data.Value |> Option.defaultValue []
        let raw = if FSharpValues.hasRawView data.Value then [ RawViewRow ] else []
        [| yield! children |> List.map ChildRow; yield! raw |]

    static let clrType (appDomain: DkmClrAppDomain) (t: IType option) =
        match t with
        | Some t -> Lmr.safe null (fun () -> DkmClrType.Create(appDomain, t.Native :?> Microsoft.VisualStudio.Debugger.Metadata.Type))
        | None -> null

    static let rawFullName (parent: FSharpResultData) =
        if isNull parent.FullName then null else parent.FullName + ", raw"

    /// The same inspection context with ShowValueRaw: the C# provider then skips proxies and
    /// DebuggerDisplay, and our GetResult steps aside (as for `x, raw`).
    static let rawContext (c: DkmInspectionContext) =
        DkmInspectionContext.Create(
            c.InspectionSession,
            c.RuntimeInstance,
            c.Thread,
            c.Timeout,
            c.EvaluationFlags ||| DkmEvaluationFlags.ShowValueRaw,
            c.FuncEvalFlags,
            c.Radix,
            c.Language,
            c.ReturnValue,
            c.AdditionalVisualizationData,
            c.AdditionalVisualizationDataPriority,
            c.ReturnValues,
            c.SymbolsConnection
        )

    /// Children of a Raw View row: the C# provider's children of the value in a raw context.
    static let rawViewChildren (raw: RawViewData) (workList: DkmWorkList) initialRequestSize context (k: DkmCompletionRoutine<DkmGetChildrenAsyncResult>) =
        let parent = raw.Parent
        let context = rawContext context

        let complete (r: DkmEvaluationAsyncResult) =
            match r.Result with
            | null -> k.Invoke(DkmGetChildrenAsyncResult.CreateErrorResult(exn $"Raw View: error 0x{r.ErrorCode:X8}"))
            | csharp -> csharp.GetChildren(workList, initialRequestSize, context, k)

        parent.ClrValue.GetResult(
            workList, parent.DeclaredType, null, context, noFormatSpecifiers, "Raw View", rawFullName parent, DkmCompletionRoutine<_>(complete))

    /// Creates the results for some rows one after another through Concord, then calls k.
    static let evaluateRows (workList: DkmWorkList) context (parent: FSharpResultData) (rows: Row[]) (k: DkmEvaluationResult[] -> unit) =
        let results = Array.zeroCreate rows.Length
        let frame = parent.ClrValue.StackFrame

        let failed name fullName message =
            DkmFailedEvaluationResult.Create(context, frame, name, fullName, message, DkmEvaluationResultFlags.None, null) :> DkmEvaluationResult

        let rec next i =
            if i = rows.Length then
                k results
            else
                let complete (r: DkmEvaluationAsyncResult) =
                    results[i] <-
                        match r.Result with
                        | null -> failed "?" null $"error 0x{r.ErrorCode:X8}"
                        | result -> result

                    next (i + 1)

                match rows[i] with
                | RawViewRow ->
                    results[i] <-
                        DkmSuccessEvaluationResult.Create(
                            context,
                            frame,
                            "Raw View",
                            rawFullName parent,
                            DkmEvaluationResultFlags.ReadOnly ||| DkmEvaluationResultFlags.Expandable,
                            "",
                            null,
                            "",
                            DkmEvaluationResultCategory.Data,
                            DkmEvaluationResultAccessType.None,
                            DkmEvaluationResultStorageType.None,
                            DkmEvaluationResultTypeModifierFlags.None,
                            null,
                            null,
                            null,
                            RawViewData parent
                        )

                    next (i + 1)
                | ChildRow child ->
                    let fullName = Option.toObj child.FullName

                    match child.Value.Value with
                    | :? ConcordValue as v ->
                        let declared = clrType v.Value.Type.AppDomain child.DeclaredType
                        v.Value.GetResult(workList, declared, null, context, noFormatSpecifiers, child.Name, fullName, DkmCompletionRoutine<_>(complete))
                    | v ->
                        results[i] <- failed child.Name fullName v.Error
                        next (i + 1)

        next 0

    interface IDkmClrResultProvider with

        member _.GetResult
            (clrValue, workList, declaredType, customTypeInfo, inspectionContext, formatSpecifiers, resultName, resultFullName, completionRoutine)
            =
            let fsharp =
                Settings.enabled ()
                && not (inspectionContext.EvaluationFlags.HasFlag DkmEvaluationFlags.ShowValueRaw)
                && not (hasSpecifier "raw" formatSpecifiers)
                && not (hasSpecifier "results" formatSpecifiers)

            let created =
                if not fsharp then
                    None
                else
                    try
                        tryCreate clrValue declaredType inspectionContext resultName resultFullName
                    with e ->
                        Log.exn "RP" $"GetResult '{resultName}'" e
                        None

            match created with
            | Some result -> completionRoutine.Invoke(DkmEvaluationAsyncResult(result))
            | None ->
                // Not an F# shape: the next provider (C#) renders it. This call does not come back here.
                let completionRoutine =
                    if not fsharp then
                        completionRoutine
                    else
                        DkmCompletionRoutine<DkmEvaluationAsyncResult>(fun r ->
                            let retyped =
                                match r.Result with
                                | null -> r
                                | result ->
                                    try
                                        DkmEvaluationAsyncResult(retype clrValue declaredType inspectionContext result)
                                    with e ->
                                        Log.exn "RP" $"Type column of '{resultName}'" e
                                        r

                            completionRoutine.Invoke retyped)

                clrValue.GetResult(
                    workList, declaredType, customTypeInfo, inspectionContext, formatSpecifiers, resultName, resultFullName, completionRoutine)

        member _.GetChildren(result, workList, initialRequestSize, inspectionContext, completionRoutine) =
            match result.GetDataItem<FSharpResultData>(), result.GetDataItem<RawViewData>() with
            | null, null ->
                let result =
                    match result.GetDataItem<RetypedData>() with
                    | null -> result
                    | retyped -> retyped.Original

                result.GetChildren(workList, initialRequestSize, inspectionContext, completionRoutine)
            | null, raw -> rawViewChildren raw workList initialRequestSize inspectionContext completionRoutine
            | data, _ ->
                try
                    let rows = rows data
                    let enumContext = DkmEvaluationResultEnumContext.Create(rows.Length, result.StackFrame, inspectionContext, RowsData(data, rows))
                    let initial = Array.truncate initialRequestSize rows

                    evaluateRows workList inspectionContext data initial (fun children ->
                        completionRoutine.Invoke(DkmGetChildrenAsyncResult(children, enumContext)))
                with e ->
                    Log.exn "RP" $"GetChildren '{result.Name}'" e
                    completionRoutine.Invoke(DkmGetChildrenAsyncResult.CreateErrorResult e)

        member _.GetItems(enumContext, workList, startIndex, count, completionRoutine) =
            match enumContext.GetDataItem<RowsData>() with
            | null -> enumContext.GetItems(workList, startIndex, count, completionRoutine)
            | data ->
                try
                    let rows = data.Rows |> Array.skip (min startIndex data.Rows.Length) |> Array.truncate count

                    evaluateRows workList enumContext.InspectionContext data.Parent rows (fun items ->
                        completionRoutine.Invoke(DkmEvaluationEnumAsyncResult(items)))
                with e ->
                    Log.exn "RP" "GetItems" e
                    completionRoutine.Invoke(DkmEvaluationEnumAsyncResult.CreateErrorResult e)

        member _.GetClrValue(successResult) =
            match successResult.GetDataItem<FSharpResultData>(), successResult.GetDataItem<RetypedData>() with
            | null, null -> successResult.GetClrValue()
            | null, retyped -> (retyped.Original :?> DkmSuccessEvaluationResult).GetClrValue()
            | data, _ -> data.ClrValue

        member _.GetUnderlyingString(result) =
            match result.GetDataItem<RetypedData>() with
            | null -> result.GetUnderlyingString()
            | retyped -> retyped.Original.GetUnderlyingString()
