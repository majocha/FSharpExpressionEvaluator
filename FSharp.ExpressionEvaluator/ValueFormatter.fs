namespace FSharp.ExpressionEvaluator

open System.Text.RegularExpressions

/// <summary>
/// Pure F#-specific value-string formatting logic.
///
/// All functions in this module work on plain strings and have no dependency
/// on VS / Concord APIs so they can be exercised directly from unit tests.
///
/// Rules applied by <see cref="formatValue"/>:
/// <list type="bullet">
///   <item><description>
///     <c>unit</c> — always shown as <c>()</c>.  The default C# EE displays
///     the <c>Unit</c> reference singleton as <c>{}</c> or an empty struct,
///     which is meaningless to F# users.
///   </description></item>
///   <item><description>
///     <c>char</c> — the C# EE prepends the numeric code point (e.g.
///     <c>65 'A'</c>).  F# convention is the character literal only
///     (<c>'A'</c>), so the leading decimal digits and space are stripped when
///     present.
///   </description></item>
/// </list>
/// </summary>
module ValueFormatter =

    // Matches the C# EE char representation: an optional integer code point
    // followed by a space and then the character literal in single quotes.
    // Examples: "65 'A'"  →  group 1 = "'A'"
    //           "'A'"     →  no match (already F# style; returned as-is)
    let private charWithCodePoint = Regex(@"^\d+ ('.+')$", RegexOptions.Compiled)

    /// <summary>
    /// Applies F#-specific formatting to a raw value string.
    /// </summary>
    /// <param name="fsharpTypeName">
    /// The F# type name already produced by <see cref="TypeNameFormatter.formatTypeName"/>.
    /// </param>
    /// <param name="rawValue">
    /// The value string returned by the default CLR formatter
    /// (<c>DkmClrValue.GetValueString</c>).
    /// </param>
    /// <returns>
    /// An F#-idiomatic value string.  Falls back to <paramref name="rawValue"/>
    /// unchanged for types that do not require special treatment.
    /// </returns>
    let formatValue (fsharpTypeName: string) (rawValue: string) : string =
        match fsharpTypeName with
        | "unit" ->
            // F# unit literal.  The CLR may display the Unit singleton as
            // "{}" or "{FSharp.Core.Unit}" – neither is meaningful in F#.
            "()"

        | "char" ->
            // Strip the numeric code-point prefix the C# EE inserts:
            //   "65 'A'"  →  "'A'"
            let m = charWithCodePoint.Match(rawValue)
            if m.Success then m.Groups.[1].Value
            else rawValue

        | _ -> rawValue
