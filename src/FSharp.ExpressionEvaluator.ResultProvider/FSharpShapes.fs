/// Recognizes F# runtime shapes from LMR metadata. M0 spike: lists only.
module internal FSharp.ExpressionEvaluator.FSharpShapes

open Microsoft.VisualStudio.Debugger.Metadata

[<Literal>]
let FSharpListTypeName = "Microsoft.FSharp.Collections.FSharpList`1"

let private genericDefinitionName (t: Type) =
    if t.IsGenericType then t.GetGenericTypeDefinition().FullName else t.FullName

let isFSharpList (t: Type) = genericDefinitionName t = FSharpListTypeName

/// F# display name for a type. M0 spike: abbreviations and postfix list only.
let rec typeName (t: Type) : string =
    match t.FullName with
    | "System.Int32" -> "int"
    | "System.Int64" -> "int64"
    | "System.Double" -> "float"
    | "System.Single" -> "float32"
    | "System.String" -> "string"
    | "System.Boolean" -> "bool"
    | "System.Char" -> "char"
    | "System.Object" -> "obj"
    | _ when isFSharpList t -> $"{typeName (t.GetGenericArguments()[0])} list"
    | _ -> t.Name
