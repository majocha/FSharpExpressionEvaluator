namespace FSharp.ExpressionEvaluator

/// Converts CLR type metadata—expressed as fully-qualified type-name strings and
/// already-formatted generic argument strings—into idiomatic F# type name strings.
///
/// All functions in this module are pure and have no dependency on VS/Concord APIs,
/// so they can be exercised directly from a unit-test project.
module TypeNameFormatter =

    // -----------------------------------------------------------------
    // CLR full-name → F# keyword/alias table
    // -----------------------------------------------------------------

    /// Maps CLR full type names to their F# keyword or standard aliases.
    let private primitives =
        readOnlyDict [
            "System.Boolean",                          "bool"
            "System.Byte",                             "byte"
            "System.SByte",                            "sbyte"
            "System.Char",                             "char"
            "System.Decimal",                          "decimal"
            "System.Double",                           "float"
            "System.Single",                           "float32"
            "System.Int16",                            "int16"
            "System.Int32",                            "int"
            "System.Int64",                            "int64"
            "System.UInt16",                           "uint16"
            "System.UInt32",                           "uint32"
            "System.UInt64",                           "uint64"
            "System.IntPtr",                           "nativeint"
            "System.UIntPtr",                          "unativeint"
            "System.String",                           "string"
            "System.Object",                           "obj"
            "Microsoft.FSharp.Core.Unit",              "unit"
            "System.Void",                             "unit"
        ]

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// Strips the generic arity suffix appended by the CLR.
    /// E.g.  "FSharpOption`1"  →  "FSharpOption"
    let private stripArity (name: string) =
        let idx = name.IndexOf('`')
        if idx >= 0 then name.Substring(0, idx) else name

    /// Builds a generic type application string using F# angle-bracket syntax.
    let private angleArgs (args: string list) = sprintf "<%s>" (String.concat ", " args)

    // -----------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------

    /// <summary>
    /// Formats a CLR type as an idiomatic F# type name.
    /// </summary>
    /// <param name="fullName">
    /// The fully-qualified CLR name of the type (namespace + class name, including
    /// the backtick-arity suffix for generic type definitions).
    /// Examples: <c>"System.Int32"</c>, <c>"Microsoft.FSharp.Core.FSharpOption`1"</c>.
    /// </param>
    /// <param name="genericArgs">
    /// The already-formatted F# names of the type's generic arguments, in order.
    /// Pass an empty list for non-generic types.
    /// </param>
    /// <returns>An idiomatic F# type name string.</returns>
    let formatTypeName (fullName: string) (genericArgs: string list) : string =
        match fullName with
        // ----- Primitives & unit -----------------------------------------------
        | p when primitives.ContainsKey(p) -> primitives.[p]

        // ----- Common F# library types -----------------------------------------

        | "Microsoft.FSharp.Core.FSharpOption`1" ->
            match genericArgs with
            | [ arg ] -> arg + " option"
            | _       -> "option"

        | "Microsoft.FSharp.Core.FSharpValueOption`1" ->
            match genericArgs with
            | [ arg ] -> arg + " voption"
            | _       -> "voption"

        | "Microsoft.FSharp.Collections.FSharpList`1" ->
            match genericArgs with
            | [ arg ] -> arg + " list"
            | _       -> "list"

        | "Microsoft.FSharp.Core.FSharpRef`1" ->
            match genericArgs with
            | [ arg ] -> arg + " ref"
            | _       -> "ref"

        | "Microsoft.FSharp.Collections.FSharpSet`1" ->
            match genericArgs with
            | [ arg ] -> sprintf "Set<%s>" arg
            | _       -> "Set"

        | "Microsoft.FSharp.Collections.FSharpMap`2" ->
            match genericArgs with
            | [ k; v ] -> sprintf "Map<%s, %s>" k v
            | _         -> "Map"

        | "Microsoft.FSharp.Core.FSharpFunc`2" ->
            // Render as  "domain -> range".  When nested as an argument the caller
            // is responsible for adding parentheses if required by outer syntax.
            match genericArgs with
            | [ dom; range ] -> sprintf "%s -> %s" dom range
            | _              -> "fun"

        | "Microsoft.FSharp.Collections.FSharpSeq`1"
        | "System.Collections.Generic.IEnumerable`1" ->
            match genericArgs with
            | [ arg ] -> arg + " seq"
            | _       -> "seq"

        // ----- Tuples (ref & struct) -------------------------------------------

        | t when t.StartsWith("System.Tuple`") || t = "System.Tuple" ->
            match genericArgs with
            | [] -> "unit"
            | args -> sprintf "(%s)" (String.concat " * " args)

        | t when t.StartsWith("System.ValueTuple`") || t = "System.ValueTuple" ->
            match genericArgs with
            | [] -> "unit"
            | args -> sprintf "struct (%s)" (String.concat " * " args)

        // ----- Common BCL generics with F#-style aliases -----------------------

        | "System.Collections.Generic.List`1" ->
            match genericArgs with
            | [ arg ] -> sprintf "ResizeArray<%s>" arg
            | _       -> "ResizeArray"

        | "System.Collections.Generic.Dictionary`2" ->
            match genericArgs with
            | [ k; v ] -> sprintf "Dictionary<%s, %s>" k v
            | _         -> "Dictionary"

        // ----- Fallback: strip namespace, keep generic args in <> syntax --------
        | _ ->
            let lastDot = fullName.LastIndexOf('.')
            let simpleName = if lastDot >= 0 then fullName.Substring(lastDot + 1) else fullName
            let baseName   = stripArity simpleName
            match genericArgs with
            | [] -> baseName
            | args -> baseName + angleArgs args

    /// <summary>
    /// Formats an array type name in F# syntax.
    /// </summary>
    /// <param name="elementName">The already-formatted element type name.</param>
    /// <param name="rank">The number of array dimensions (1 for a simple vector).</param>
    let formatArrayType (elementName: string) (rank: int) : string =
        if rank <= 1 then
            elementName + "[]"
        else
            // Multi-dimensional: int[,], int[,,], …
            elementName + "[" + String.replicate (rank - 1) "," + "]"
