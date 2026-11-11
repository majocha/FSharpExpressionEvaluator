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

/// Attached to a copy of a C# result that only changes its Type column to F# names, or a sequence's
/// Value column to its F# preview; children, the CLR value and the underlying string come from the
/// C# original. For an F# sequence, `Sequence` describes the value for its Raw View row.
[<AllowNullLiteral>]
type internal RetypedData(original: DkmEvaluationResult, sequence: FSharpResultData) =
    inherit DkmDataItem()
    member _.Original = original
    member _.Sequence = sequence

/// The rows under an F# sequence (Results View, Raw View), already created.
[<AllowNullLiteral>]
type internal SequenceRowsData(rows: DkmEvaluationResult[]) =
    inherit DkmDataItem()
    member _.Rows = rows

/// The Locals window's `[compiler generated]` row (docs/DESIGN.md §6.4): its value is a null `obj`, its
/// children are the compiler temporaries, each evaluated as an expression.
[<AllowNullLiteral>]
type internal CompilerGeneratedData(clrValue: DkmClrValue, names: string list) =
    inherit DkmDataItem()
    member _.ClrValue = clrValue
    member _.Names = names

/// The full name of the result whose rows another provider (C#) lists, kept on its enumeration
/// context to log the rows' full names (Log.fullNames).
[<AllowNullLiteral>]
type internal ParentNameData(fullName: string) =
    inherit DkmDataItem()
    member _.FullName = fullName

/// The rows under an F# value, kept on its enumeration context. They are made when GetItems reads
/// them: an array can have millions of elements.
[<AllowNullLiteral>]
type internal RowsData(parent: FSharpResultData, rows: Paged<Row>) =
    inherit DkmDataItem()
    member _.Parent = parent
    member _.Rows = rows

/// A result by the data item this component attached to it, if any (a result has one at most).
type internal ResultKind =
    | FSharpResult of FSharpResultData
    | RawViewResult of RawViewData
    | CompilerGeneratedResult of CompilerGeneratedData
    | RetypedResult of RetypedData
    /// Created by another provider (C#).
    | OtherResult

    static member Of(result: DkmEvaluationResult) =
        match result.GetDataItem<FSharpResultData>() with
        | null ->
            match result.GetDataItem<RawViewData>() with
            | null ->
                match result.GetDataItem<CompilerGeneratedData>() with
                | null ->
                    match result.GetDataItem<RetypedData>() with
                    | null -> OtherResult
                    | retyped -> RetypedResult retyped
                | data -> CompilerGeneratedResult data
            | raw -> RawViewResult raw
        | data -> FSharpResult data

/// F# result provider (docs/DESIGN.md §5.1): renders F# shapes from memory and chains every other
/// value to the next IDkmClrResultProvider (Roslyn's C# one). Children of F# values are created
/// through Concord again, so each lands with whichever provider owns its shape. While the F# EE is
/// switched off (Settings.fs), new values all go to C#; results created before still expand as F#.
type ResultProvider() =

    static let log = Log.write "RP"
    static do log "ResultProvider loaded"

    /// The preview options for an inspection context: hex integers when the debugger's hex display is on.
    static let optionsFor (context: DkmInspectionContext) = FSharpValues.defaultOptions |> FSharpValues.withRadix context.Radix
    static let noFormatSpecifiers = ReadOnlyCollection<string>([||])

    static let hasSpecifier (name: string) (specifiers: ReadOnlyCollection<string>) =
        not (isNull specifiers) && specifiers.Contains name

    static let lmrType (t: DkmClrType) =
        if isNull t then None else Lmr.safe None (fun () -> Some(LmrType.Of t))

    /// A payload of the expression compiler's custom type info, decoded by `tryDecode`.
    static let decodeTypeInfo what (tryDecode: System.Guid -> byte[] -> 'T option) (customTypeInfo: DkmClrCustomTypeInfo) =
        match customTypeInfo with
        | null -> None
        | info ->
            try
                tryDecode info.PayloadTypeId (Seq.toArray info.Payload)
            with e ->
                Log.exn "RP" what e
                None

    /// The F# type of an expression's result, from the expression compiler (docs/DESIGN.md §6.8).
    static let fsharpTypeName = decodeTypeInfo "F# type info" FSharpTypes.TypeInfo.tryDecode

    /// The compiler temporaries of the `[compiler generated]` row, from the expression compiler.
    static let compilerGeneratedNames =
        decodeTypeInfo "compiler generated names" FSharpTypes.CompilerGenerated.tryDecode

    /// A row that only groups the rows under it: no value and no type of its own.
    static let groupRow context frame name fullName (data: DkmDataItem) =
        DkmSuccessEvaluationResult.Create(
            context,
            frame,
            name,
            fullName,
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
            data
        )

    /// Rows created up front as the children of a result: the first `initialRequestSize` of them
    /// now, the rest through GetItems.
    static let staticRows (rows: DkmEvaluationResult[]) initialRequestSize frame context =
        let enumContext = DkmEvaluationResultEnumContext.Create(rows.Length, frame, context, SequenceRowsData rows)
        DkmGetChildrenAsyncResult(Array.truncate initialRequestSize rows, enumContext)

    /// The `count` rows from `startIndex` on, as GetItems asks for them.
    static let page startIndex count (rows: 'T[]) =
        rows |> Array.skip (min startIndex rows.Length) |> Array.truncate count

    /// The `[compiler generated]` row: no full name, as no expression stands for the group.
    static let compilerGeneratedRow (clrValue: DkmClrValue) context name names =
        groupRow context clrValue.StackFrame name null (CompilerGeneratedData(clrValue, names))

    /// Children of the `[compiler generated]` row: each temporary as an expression for the debugger
    /// to evaluate, as the Watch window does, so each row has its value, its full name and edits. This
    /// component can't evaluate them itself: it is below the expression evaluators (component level
    /// 63000), and Concord passes its calls only to components below it (E_NOTIMPL).
    static let compilerGeneratedChildren (data: CompilerGeneratedData) initialRequestSize (context: DkmInspectionContext) =
        let frame = data.ClrValue.StackFrame

        let children =
            data.Names
            |> List.map (fun name ->
                DkmIntermediateEvaluationResult.Create(
                    context,
                    frame,
                    name,
                    name,
                    name,
                    context.Language,
                    frame.RuntimeInstance,
                    null
                )
                :> DkmEvaluationResult)
            |> Array.ofList

        staticRows children initialRequestSize frame context

    /// The Type column; `fsharpName`, the expression's F# type, replaces the declared type's name.
    static let typeColumn (fsharpName: string option) (declared: IType option) (value: IValue) =
        let declaredName d = fsharpName |> Option.defaultWith (fun () -> FSharpTypes.typeName d)

        match declared with
        | Some d when not value.IsNull && FSharpTypes.shape d = FSharpTypes.NotFSharp ->
            // e.g. a list held in an `obj`: show both, as the C# provider does.
            $"{declaredName d} {{{FSharpTypes.typeName (FSharpTypes.displayType value.Type)}}}"
        | _ -> fsharpName |> Option.defaultWith (fun () -> FSharpValues.typeNameOf declared value)

    static let tryCreate (clrValue: DkmClrValue) (declaredType: DkmClrType) fsharpName context name fullName =
        let value = ConcordValue(clrValue, context) :> IValue
        let declared = lmrType declaredType

        FSharpValues.tryPreview (optionsFor context) declared value
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
                typeColumn fsharpName declared value,
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
    /// mentions FSharp.Core (`System.Threading.Tasks.Task<unit>`), and an F# preview for sequences
    /// (`seq [1; 2; 3]`), whose Results View stays C#'s.
    static let retype (clrValue: DkmClrValue) (declaredType: DkmClrType) fsharpName context (result: DkmEvaluationResult) =
        match result with
        | :? DkmSuccessEvaluationResult as s ->
            let value = ConcordValue(clrValue, context) :> IValue

            let typeName =
                FSharpValues.csharpTypeColumn fsharpName (lmrType declaredType) value |> Option.defaultValue s.Type
            let options = optionsFor context
            let seqPreview = FSharpValues.trySeqPreview options value

            // A primitive keeps C#'s result (its flags, string visualizer and underlying string) with
            // F# literals as its value and editable value.
            let primitive =
                if seqPreview.IsSome || s.Flags.HasFlag DkmEvaluationResultFlags.ExceptionThrown then
                    None
                else
                    FSharpValues.tryPrimitiveColumns options.Hex value

            let preview =
                match seqPreview, primitive with
                | Some p, _ -> p
                | None, Some(p, _) -> p
                | None, None -> s.Value

            let editableValue =
                match primitive with
                | Some(_, editable) when not (isNull s.EditableValue) -> editable
                | _ -> s.EditableValue

            let sequence =
                if seqPreview.IsSome then FSharpResultData(clrValue, value, declaredType, s.FullName) else null

            if typeName <> s.Type || preview <> s.Value || editableValue <> s.EditableValue then
                DkmSuccessEvaluationResult.Create(
                    s.InspectionContext,
                    s.StackFrame,
                    s.Name,
                    s.FullName,
                    s.Flags,
                    preview,
                    editableValue,
                    typeName,
                    s.Category,
                    s.Access,
                    s.StorageType,
                    s.TypeModifierFlags,
                    s.Address,
                    s.CustomUIVisualizers,
                    s.ExternalModules,
                    RetypedData(s, sequence)
                )
                :> DkmEvaluationResult
            else
                result
        | _ -> result

    /// The rows under an F# value: its children, then the Raw View row if it has one.
    static let rows context (data: FSharpResultData) : Paged<Row> =
        let fullName = Option.ofObj data.FullName

        let children =
            FSharpValues.tryChildPages (optionsFor context) fullName data.Value
            |> Option.defaultWith (fun () -> Paged.ofList [])

        let raw = if FSharpValues.hasRawView data.Value then 1 else 0

        { Count = children.Count + raw
          Item = fun i -> if i < children.Count then ChildRow(children.Item i) else RawViewRow }

    static let clrType (appDomain: DkmClrAppDomain) (t: IType option) =
        match t with
        | Some t -> Lmr.safe null (fun () -> DkmClrType.Create(appDomain, t.Native :?> Microsoft.VisualStudio.Debugger.Metadata.Type))
        | None -> null

    static let rawFullName (parent: FSharpResultData) =
        if isNull parent.FullName then null else parent.FullName + ", raw"

    /// The same inspection context with ShowValueRaw: the C# provider then skips proxies and
    /// DebuggerDisplay, and our GetResult steps aside (as for `x, raw`).
    static let rawContext = WorkLists.withEvaluationFlags DkmEvaluationFlags.ShowValueRaw

    /// Children of a Raw View row: the C# provider's children of the value in a raw context.
    static let rawViewChildren (raw: RawViewData) (workList: DkmWorkList) initialRequestSize context (k: DkmCompletionRoutine<DkmGetChildrenAsyncResult>) =
        let parent = raw.Parent
        let context = rawContext context

        let children (r: DkmEvaluationAsyncResult) =
            match r.Result with
            | null -> k.Invoke(DkmGetChildrenAsyncResult.CreateErrorResult(exn $"Raw View: error 0x{r.ErrorCode:X8}"))
            | csharp ->
                WorkLists.complete "RP" (fun () -> "Raw View children") workList (fun workList k -> csharp.GetChildren(workList, initialRequestSize, context, k)) k.Invoke

        WorkLists.complete
            "RP"
            (fun () -> "Raw View")
            workList
            (fun workList k ->
                parent.ClrValue.GetResult(workList, parent.DeclaredType, null, context, noFormatSpecifiers, "Raw View", rawFullName parent, k))
            children

    /// The Raw View row of a value. It has no value of its own; its children come from rawViewChildren.
    static let rawViewRow context frame (parent: FSharpResultData) =
        groupRow context frame "Raw View" (rawFullName parent) (RawViewData parent) :> DkmEvaluationResult

    /// Children of an F# sequence: C#'s Results View, then a Raw View holding the fields of its
    /// closure class (`start`/`stop` of a range, `pc`/`current` of a `seq { }`), which C# lists first.
    static let sequenceChildren (retyped: RetypedData) workList initialRequestSize (context: DkmInspectionContext) (k: DkmCompletionRoutine<DkmGetChildrenAsyncResult>) =
        let parent = retyped.Sequence
        let frame = parent.ClrValue.StackFrame

        let isResultsView (r: DkmEvaluationResult) =
            (not (isNull r.FullName) && r.FullName.EndsWith ", results") || r.Name = "Results View"

        let complete (r: DkmGetChildrenAsyncResult) =
            if isNull r.InitialChildren then
                k.Invoke r
            else
                let rows = [| yield! r.InitialChildren |> Array.filter isResultsView; rawViewRow context frame parent |]
                k.Invoke(staticRows rows initialRequestSize frame context)

        // A closure has a handful of fields, so one request returns C#'s Results View row.
        WorkLists.complete "RP" (fun () -> "sequence children") workList (fun workList k -> retyped.Original.GetChildren(workList, 100, context, k)) complete

    /// Logs the full names of the rows listed under a result, for eng\Probe-Debugger.ps1 (Log.fullNames).
    static let logFullNames (parentFullName: string) (rows: DkmEvaluationResult[]) =
        let parent = if isNull parentFullName then "(none)" else parentFullName

        if Log.fullNames.Value && not (isNull rows) then
            for r in rows do
                if not (isNull r) then
                    let readOnly =
                        match r with
                        | :? DkmSuccessEvaluationResult as s when s.Flags.HasFlag DkmEvaluationResultFlags.ReadOnly -> " (read-only)"
                        | _ -> ""

                    let fullName = if isNull r.FullName then "(none)" else $"<{r.FullName}>"
                    log $"full name of '{parent}' / '{r.Name}'{readOnly}: {fullName}"

    /// A row that can't be edited: the same result with the ReadOnly flag, for a C# result (a
    /// primitive under an F# value) whose full name is no target of an assignment. Its children, CLR
    /// value and underlying string still come from the original.
    static let readOnly (result: DkmEvaluationResult) =
        match result with
        | :? DkmSuccessEvaluationResult as s when not (s.Flags.HasFlag DkmEvaluationResultFlags.ReadOnly) ->
            let data =
                match ResultKind.Of s with
                | RetypedResult retyped -> Some(RetypedData(retyped.Original, retyped.Sequence))
                | OtherResult -> Some(RetypedData(s, null))
                | _ -> None

            match data with
            | None -> result
            | Some data ->
                DkmSuccessEvaluationResult.Create(
                    s.InspectionContext,
                    s.StackFrame,
                    s.Name,
                    s.FullName,
                    s.Flags ||| DkmEvaluationResultFlags.ReadOnly,
                    s.Value,
                    s.EditableValue,
                    s.Type,
                    s.Category,
                    s.Access,
                    s.StorageType,
                    s.TypeModifierFlags,
                    s.Address,
                    s.CustomUIVisualizers,
                    s.ExternalModules,
                    data
                )
                :> DkmEvaluationResult
        | _ -> result

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
                let complete editable (r: DkmEvaluationAsyncResult) =
                    results[i] <-
                        match r.Result with
                        | null -> failed "?" null $"error 0x{r.ErrorCode:X8}"
                        | result when editable -> result
                        | result -> readOnly result

                    next (i + 1)

                match rows[i] with
                | RawViewRow ->
                    results[i] <- rawViewRow context frame parent
                    next (i + 1)
                | ChildRow child ->
                    let fullName = Option.toObj child.FullName

                    match child.Value.Value with
                    | :? ConcordValue as v ->
                        let declared = clrType v.Value.Type.AppDomain child.DeclaredType

                        WorkLists.complete
                            "RP"
                            (fun () -> $"row '{child.Name}'")
                            workList
                            (fun workList k -> v.Value.GetResult(workList, declared, null, context, noFormatSpecifiers, child.Name, fullName, k))
                            (complete child.Editable)
                    | v ->
                        results[i] <- failed child.Name fullName v.Error
                        next (i + 1)

        next 0

    interface IDkmClrResultProvider with

        member _.GetResult
            (clrValue, workList, declaredType, customTypeInfo, inspectionContext, formatSpecifiers, resultName, resultFullName, completionRoutine)
            =
            let struct (call, completionRoutine) = WorkLists.traced "RP" (fun () -> $"GetResult '{resultName}'") completionRoutine
            use _ = call

            let fsharp =
                Settings.enabled ()
                && not (inspectionContext.EvaluationFlags.HasFlag DkmEvaluationFlags.ShowValueRaw)
                && not (hasSpecifier "raw" formatSpecifiers)
                && not (hasSpecifier "results" formatSpecifiers)

            let fsharpName = if fsharp then fsharpTypeName customTypeInfo else None

            let created =
                if not fsharp then
                    None
                else
                    match compilerGeneratedNames customTypeInfo with
                    | Some names -> Some(compilerGeneratedRow clrValue inspectionContext resultName names)
                    | None ->
                        try
                            tryCreate clrValue declaredType fsharpName inspectionContext resultName resultFullName
                        with e ->
                            Log.exn "RP" $"GetResult '{resultName}'" e
                            None

            match created with
            | Some result -> completionRoutine.Invoke(DkmEvaluationAsyncResult(result))
            | None ->
                // Not an F# shape: the next provider (C#) renders it. This call does not come back here,
                // and it runs to completion before we return (WorkLists.fs).
                let retyped (r: DkmEvaluationAsyncResult) =
                    match r.Result with
                    | null -> r
                    | result ->
                        try
                            DkmEvaluationAsyncResult(retype clrValue declaredType fsharpName inspectionContext result)
                        with e ->
                            Log.exn "RP" $"Type column of '{resultName}'" e
                            r

                let csharp workList completionRoutine =
                    clrValue.GetResult(
                        workList, declaredType, customTypeInfo, inspectionContext, formatSpecifiers, resultName, resultFullName, completionRoutine)

                WorkLists.complete "RP" (fun () -> $"GetResult '{resultName}'") workList csharp (fun r ->
                    completionRoutine.Invoke(if fsharp then retyped r else r))

        member _.GetChildren(result, workList, initialRequestSize, inspectionContext, completionRoutine) =
            let struct (call, completionRoutine) = WorkLists.traced "RP" (fun () -> $"GetChildren '{result.Name}'") completionRoutine
            use _ = call

            let orError f =
                try
                    f ()
                with e ->
                    Log.exn "RP" $"GetChildren '{result.Name}'" e
                    completionRoutine.Invoke(DkmGetChildrenAsyncResult.CreateErrorResult e)

            let passOn (original: DkmEvaluationResult) =
                WorkLists.complete "RP" (fun () -> "GetChildren") workList (fun workList k ->
                    original.GetChildren(workList, initialRequestSize, inspectionContext, k)) (fun r ->
                    if Log.fullNames.Value && not (isNull r.EnumContext) then
                        logFullNames result.FullName r.InitialChildren
                        r.EnumContext.SetDataItem(DkmDataCreationDisposition.CreateAlways, ParentNameData result.FullName)

                    completionRoutine.Invoke r)

            match ResultKind.Of result with
            | OtherResult -> passOn result
            | RetypedResult retyped when isNull retyped.Sequence -> passOn retyped.Original
            | RetypedResult retyped ->
                orError (fun () -> sequenceChildren retyped workList initialRequestSize inspectionContext completionRoutine)
            | CompilerGeneratedResult data ->
                orError (fun () -> completionRoutine.Invoke(compilerGeneratedChildren data initialRequestSize inspectionContext))
            | RawViewResult raw -> rawViewChildren raw workList initialRequestSize inspectionContext completionRoutine
            | FSharpResult data ->
                orError (fun () ->
                    let rows = rows inspectionContext data
                    let enumContext = DkmEvaluationResultEnumContext.Create(rows.Count, result.StackFrame, inspectionContext, RowsData(data, rows))
                    let initial = Paged.page 0 initialRequestSize rows

                    evaluateRows workList inspectionContext data initial (fun children ->
                        logFullNames result.FullName children
                        completionRoutine.Invoke(DkmGetChildrenAsyncResult(children, enumContext))))

        member _.GetItems(enumContext, workList, startIndex, count, completionRoutine) =
            let struct (call, completionRoutine) = WorkLists.traced "RP" (fun () -> $"GetItems {startIndex}+{count}") completionRoutine
            use _ = call

            match enumContext.GetDataItem<RowsData>(), enumContext.GetDataItem<SequenceRowsData>() with
            | null, null ->
                WorkLists.complete "RP" (fun () -> "GetItems") workList (fun workList k ->
                    enumContext.GetItems(workList, startIndex, count, k)) (fun r ->
                    match enumContext.GetDataItem<ParentNameData>() with
                    | null -> ()
                    | parent -> logFullNames parent.FullName r.Items

                    completionRoutine.Invoke r)
            | null, sequence ->
                completionRoutine.Invoke(DkmEvaluationEnumAsyncResult(page startIndex count sequence.Rows))
            | data, _ ->
                try
                    evaluateRows workList enumContext.InspectionContext data.Parent (Paged.page startIndex count data.Rows) (fun items ->
                        logFullNames data.Parent.FullName items
                        completionRoutine.Invoke(DkmEvaluationEnumAsyncResult(items)))
                with e ->
                    Log.exn "RP" "GetItems" e
                    completionRoutine.Invoke(DkmEvaluationEnumAsyncResult.CreateErrorResult e)

        member _.GetClrValue(successResult) =
            match ResultKind.Of successResult with
            | FSharpResult data -> data.ClrValue
            | CompilerGeneratedResult data -> data.ClrValue
            | RetypedResult retyped -> (retyped.Original :?> DkmSuccessEvaluationResult).GetClrValue()
            | RawViewResult _
            | OtherResult -> successResult.GetClrValue()

        member _.GetUnderlyingString(result) =
            match result.GetDataItem<RetypedData>() with
            | null -> result.GetUnderlyingString()
            | retyped -> retyped.Original.GetUnderlyingString()
