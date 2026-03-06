namespace FSharp.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Metadata

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

module private FormatterHelpers =

    /// Recursively converts a <see cref="Microsoft.VisualStudio.Debugger.Metadata.Type"/>
    /// value (an LMR type representing a type in the debugged process) into an
    /// idiomatic F# type-name string.
    ///
    /// The conversion is delegated to <see cref="TypeNameFormatter.formatTypeName"/>
    /// so the core logic can be tested independently without VS APIs.
    let rec fsharpTypeName (lmrType: Type) : string =
        if lmrType = null then
            "?"
        elif lmrType.IsArray then
            let elem = fsharpTypeName (lmrType.GetElementType())
            TypeNameFormatter.formatArrayType elem (lmrType.GetArrayRank())
        elif lmrType.IsByRef then
            fsharpTypeName (lmrType.GetElementType()) + " byref"
        elif lmrType.IsGenericType then
            let args =
                lmrType.GetGenericArguments()
                |> Array.toList
                |> List.map fsharpTypeName
            // For generic types FullName may be null or contain assembly-qualified
            // argument names that are not useful; build the base name from
            // Namespace + Name (which retains the arity suffix, e.g. "`1").
            let ns   = lmrType.Namespace
            let name = lmrType.Name
            let fullBaseName =
                if ns <> null && ns.Length > 0 then ns + "." + name else name
            TypeNameFormatter.formatTypeName fullBaseName args
        else
            let ns   = lmrType.Namespace
            let name = lmrType.Name
            let fullName =
                if ns <> null && ns.Length > 0 then ns + "." + name else name
            TypeNameFormatter.formatTypeName fullName []

// ---------------------------------------------------------------------------
// Formatter component
// ---------------------------------------------------------------------------

/// <summary>
/// F#-aware implementation of <see cref="IDkmClrFormatter"/>.
///
/// Registered with the VS/Concord component system via <c>Formatter.vsdconfigxml</c>,
/// filtered to the F# language ID so it only affects F# debugging sessions.
///
/// Responsibilities
/// ----------------
/// • <b>GetTypeName</b>: converts CLR type names to idiomatic F# syntax
///   (e.g. <c>FSharpOption&lt;int&gt;</c> → <c>int option</c>,
///         <c>Tuple&lt;int,string&gt;</c> → <c>(int * string)</c>).
/// • <b>GetValueString / HasUnderlyingString / GetUnderlyingString</b>:
///   delegate to the default evaluator unchanged; F#-specific value
///   formatting is a planned future enhancement (see ExpressionCompiler.fs).
/// </summary>
type Formatter() =
    interface IDkmClrFormatter with

        member _.GetTypeName(inspectionContext, clrType, customTypeInfo, formatSpecifiers) =
            try
                let lmrType = clrType.GetLmrType()
                FormatterHelpers.fsharpTypeName lmrType
            with _ ->
                // Fall back to the default formatter if LMR type inspection fails.
                inspectionContext.GetTypeName(clrType, customTypeInfo, formatSpecifiers)

        member _.GetValueString(clrValue, inspectionContext, formatSpecifiers) =
            clrValue.GetValueString(inspectionContext, formatSpecifiers)

        member _.HasUnderlyingString(clrValue, inspectionContext) =
            clrValue.HasUnderlyingString inspectionContext

        member _.GetUnderlyingString(clrValue, inspectionContext) =
            clrValue.GetUnderlyingString inspectionContext
