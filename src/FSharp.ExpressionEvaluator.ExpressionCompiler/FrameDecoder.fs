namespace FSharp.ExpressionEvaluator

open System
open System.Reflection.Metadata
open Microsoft.FSharp.NativeInterop
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.CallStack
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open FSharp.ExpressionEvaluator.MethodNames

/// F# call stack and breakpoint names (docs/DESIGN.md §7). Works from module metadata only, and
/// leaves the F# compiler to a thread-pool thread: naming an F# frame starts building the
/// AppDomain's evaluation session there (§6.9). Anything it cannot name is passed on to the next
/// decoder (C#), and so is everything while the F# EE is switched off (Settings.fs).
type FrameDecoder() =

    static let log = Log.write "FD"

    static let startSession (address: DkmClrInstructionAddress) =
        try
            Debuggee.warmUp address
        with e ->
            Log.exn "FD" "warm-up" e

    static let metadata (m: DkmClrModuleInstance) =
        let mutable size = 0u
        let ptr = m.GetMetaDataBytesPtr(&size)
        MetadataReader(NativePtr.ofNativeInt ptr, int size)

    static let hasGenericParameters (md: MetadataReader) (token: int) =
        let m = md.GetMethodDefinition(Ecma335.MetadataTokens.MethodDefinitionHandle(token &&& 0xFFFFFF))
        m.GetGenericParameters().Count > 0 || (md.GetTypeDefinition(m.GetDeclaringType())).GetGenericParameters().Count > 0

    /// The frame's actual generic arguments (type's, then method's), when the method is generic.
    static let withTypeArguments (frame: DkmStackWalkFrame) (workList: DkmWorkList) md token (k: MetadataTypes.SigType list option -> unit) =
        if hasGenericParameters md token then
            WorkLists.complete "FD" "generic arguments" workList (fun workList k -> frame.GetClrGenericParameters(workList, k)) (fun r ->
                let parsed =
                    if r.ErrorCode = 0 && not (isNull r.ParameterTypeNames) then
                        let names = List.ofSeq r.ParameterTypeNames
                        let types = names |> List.choose MetadataTypes.parseTypeName
                        if types.Length = names.Length then Some types else None
                    else
                        None

                k parsed)
        else
            k None

    static let withArgumentValues
        (context: DkmInspectionContext)
        (workList: DkmWorkList)
        (frame: DkmStackWalkFrame)
        (flags: DkmVariableInfoFlags)
        (k: string option list option -> unit)
        =
        if flags.HasFlag DkmVariableInfoFlags.Values then
            // Argument values are one-line previews from the result providers (ours for F# values).
            let context =
                DkmInspectionContext.Create(
                    context.InspectionSession,
                    context.RuntimeInstance,
                    context.Thread,
                    context.Timeout,
                    context.EvaluationFlags ||| DkmEvaluationFlags.NoExpansion,
                    context.FuncEvalFlags,
                    context.Radix,
                    context.Language,
                    context.ReturnValue,
                    context.AdditionalVisualizationData,
                    context.AdditionalVisualizationDataPriority,
                    context.ReturnValues,
                    context.SymbolsConnection
                )

            WorkLists.complete "FD" "argument values" workList (fun workList k -> context.GetFrameArguments(workList, frame, k)) (fun r ->
                if r.ErrorCode = 0 && not (isNull r.Arguments) then
                    let values =
                        [ for a in r.Arguments ->
                              match a with
                              | :? DkmSuccessEvaluationResult as s -> Some s.Value
                              | _ -> None ]

                    for a in r.Arguments do
                        a.Close()

                    k (Some values)
                else
                    k None)
        else
            k None

    interface IDkmLanguageFrameDecoder with

        member _.GetFrameName(inspectionContext, workList, frame, argumentFlags, completionRoutine) =
            let call = Log.trace "FD" $"GetFrameName [{argumentFlags}]"
            use _ = { new System.IDisposable with member _.Dispose() = call.Returned() }

            let completionRoutine =
                DkmCompletionRoutine<DkmGetFrameNameAsyncResult>(fun r ->
                    call.Completed()
                    completionRoutine.Invoke r)

            // C#'s name, run to completion before we return, as every call we pass on (WorkLists.fs).
            let csharpName () =
                WorkLists.complete "FD" "C# frame name" workList (fun workList k ->
                    inspectionContext.GetFrameName(workList, frame, argumentFlags, k)) completionRoutine.Invoke

            match frame.InstructionAddress with
            | :? DkmClrInstructionAddress as address when Settings.enabled () ->
                startSession address

                try
                    let md = metadata address.ModuleInstance
                    let token = address.MethodId.Token

                    withTypeArguments frame workList md token (fun typeArguments ->
                        withArgumentValues inspectionContext workList frame argumentFlags (fun values ->
                            let format =
                                {
                                    Names = argumentFlags.HasFlag DkmVariableInfoFlags.Names
                                    Types = argumentFlags.HasFlag DkmVariableInfoFlags.Types
                                    Values = values
                                }

                            let name =
                                try
                                    Some(MethodNames.frameName md token format typeArguments)
                                with e ->
                                    Log.exn "FD" "GetFrameName" e
                                    None

                            match name with
                            | Some name ->
                                log $"GetFrameName [{argumentFlags}] {name}"
                                completionRoutine.Invoke(DkmGetFrameNameAsyncResult(name))
                            | None -> csharpName ()))
                with e ->
                    Log.exn "FD" "GetFrameName" e
                    csharpName ()
            | _ -> csharpName ()

        member _.GetFrameReturnType(inspectionContext, workList, frame, completionRoutine) =
            let call = Log.trace "FD" "GetFrameReturnType"
            use _ = { new System.IDisposable with member _.Dispose() = call.Returned() }

            let completionRoutine =
                DkmCompletionRoutine<DkmGetFrameReturnTypeAsyncResult>(fun r ->
                    call.Completed()
                    completionRoutine.Invoke r)

            let csharpReturnType () =
                WorkLists.complete "FD" "C# return type" workList (fun workList k ->
                    inspectionContext.GetFrameReturnType(workList, frame, k)) completionRoutine.Invoke

            match frame.InstructionAddress with
            | :? DkmClrInstructionAddress as address when Settings.enabled () ->
                try
                    let md = metadata address.ModuleInstance
                    let token = address.MethodId.Token

                    withTypeArguments frame workList md token (fun typeArguments ->
                        completionRoutine.Invoke(DkmGetFrameReturnTypeAsyncResult(MethodNames.returnTypeName md token typeArguments)))
                with e ->
                    Log.exn "FD" "GetFrameReturnType" e
                    csharpReturnType ()
            | _ -> csharpReturnType ()

    interface IDkmLanguageInstructionDecoder with

        member _.GetMethodName(languageInstructionAddress, argumentFlags) =
            match languageInstructionAddress.Address with
            | :? DkmClrInstructionAddress as address when Settings.enabled () ->
                try
                    let format =
                        {
                            Names = argumentFlags.HasFlag DkmVariableInfoFlags.Names
                            Types = argumentFlags.HasFlag DkmVariableInfoFlags.Types
                            Values = None
                        }

                    MethodNames.frameName (metadata address.ModuleInstance) address.MethodId.Token format None
                with e ->
                    Log.exn "FD" "GetMethodName" e
                    languageInstructionAddress.GetMethodName argumentFlags
            | _ -> languageInstructionAddress.GetMethodName argumentFlags
