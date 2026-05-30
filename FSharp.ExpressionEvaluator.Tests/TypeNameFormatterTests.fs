module FSharp.ExpressionEvaluator.Tests.TypeNameFormatterTests

open Xunit
open FSharp.ExpressionEvaluator.TypeNameFormatter


// ---------------------------------------------------------------------------
// Primitive and keyword aliases
// ---------------------------------------------------------------------------

[<Fact>]
let ``int maps to int`` () =
    Assert.Equal("int", formatTypeName "System.Int32" [])

[<Fact>]
let ``string maps to string`` () =
    Assert.Equal("string", formatTypeName "System.String" [])

[<Fact>]
let ``bool maps to bool`` () =
    Assert.Equal("bool", formatTypeName "System.Boolean" [])

[<Fact>]
let ``float maps to float`` () =
    Assert.Equal("float", formatTypeName "System.Double" [])

[<Fact>]
let ``float32 maps to float32`` () =
    Assert.Equal("float32", formatTypeName "System.Single" [])

[<Fact>]
let ``obj maps to obj`` () =
    Assert.Equal("obj", formatTypeName "System.Object" [])

[<Fact>]
let ``unit maps to unit`` () =
    Assert.Equal("unit", formatTypeName "Microsoft.FSharp.Core.Unit" [])

[<Fact>]
let ``System.Void maps to unit`` () =
    Assert.Equal("unit", formatTypeName "System.Void" [])

[<Fact>]
let ``byte maps to byte`` () =
    Assert.Equal("byte", formatTypeName "System.Byte" [])

[<Fact>]
let ``sbyte maps to sbyte`` () =
    Assert.Equal("sbyte", formatTypeName "System.SByte" [])

[<Fact>]
let ``nativeint maps to nativeint`` () =
    Assert.Equal("nativeint", formatTypeName "System.IntPtr" [])

[<Fact>]
let ``char maps to char`` () =
    Assert.Equal("char", formatTypeName "System.Char" [])

[<Fact>]
let ``decimal maps to decimal`` () =
    Assert.Equal("decimal", formatTypeName "System.Decimal" [])

// ---------------------------------------------------------------------------
// F# standard library types
// ---------------------------------------------------------------------------

[<Fact>]
let ``FSharpOption formats as postfix option`` () =
    Assert.Equal("int option", formatTypeName "Microsoft.FSharp.Core.FSharpOption`1" [ "int" ])

[<Fact>]
let ``FSharpOption with complex arg`` () =
    Assert.Equal("string list option",
        formatTypeName "Microsoft.FSharp.Core.FSharpOption`1" [ "string list" ])

[<Fact>]
let ``FSharpValueOption formats as postfix voption`` () =
    Assert.Equal("int voption", formatTypeName "Microsoft.FSharp.Core.FSharpValueOption`1" [ "int" ])

[<Fact>]
let ``FSharpList formats as postfix list`` () =
    Assert.Equal("string list", formatTypeName "Microsoft.FSharp.Collections.FSharpList`1" [ "string" ])

[<Fact>]
let ``FSharpRef formats as postfix ref`` () =
    Assert.Equal("int ref", formatTypeName "Microsoft.FSharp.Core.FSharpRef`1" [ "int" ])

[<Fact>]
let ``FSharpSet formats with angle brackets`` () =
    Assert.Equal("Set<int>", formatTypeName "Microsoft.FSharp.Collections.FSharpSet`1" [ "int" ])

[<Fact>]
let ``FSharpMap formats with angle brackets`` () =
    Assert.Equal("Map<string, int>",
        formatTypeName "Microsoft.FSharp.Collections.FSharpMap`2" [ "string"; "int" ])

[<Fact>]
let ``FSharpFunc formats as arrow`` () =
    Assert.Equal("int -> string",
        formatTypeName "Microsoft.FSharp.Core.FSharpFunc`2" [ "int"; "string" ])

[<Fact>]
let ``IEnumerable formats as seq`` () =
    Assert.Equal("float seq",
        formatTypeName "System.Collections.Generic.IEnumerable`1" [ "float" ])

// ---------------------------------------------------------------------------
// Tuple types
// ---------------------------------------------------------------------------

[<Fact>]
let ``System.Tuple2 formats with star notation`` () =
    Assert.Equal("(int * string)",
        formatTypeName "System.Tuple`2" [ "int"; "string" ])

[<Fact>]
let ``System.Tuple3 formats with star notation`` () =
    Assert.Equal("(int * string * bool)",
        formatTypeName "System.Tuple`3" [ "int"; "string"; "bool" ])

[<Fact>]
let ``System.ValueTuple2 formats as struct tuple`` () =
    Assert.Equal("struct (int * string)",
        formatTypeName "System.ValueTuple`2" [ "int"; "string" ])

[<Fact>]
let ``System.ValueTuple3 formats as struct tuple`` () =
    Assert.Equal("struct (int * string * bool)",
        formatTypeName "System.ValueTuple`3" [ "int"; "string"; "bool" ])

// ---------------------------------------------------------------------------
// BCL generic types with F#-specific aliases
// ---------------------------------------------------------------------------

[<Fact>]
let ``List<T> formats as ResizeArray`` () =
    Assert.Equal("ResizeArray<int>",
        formatTypeName "System.Collections.Generic.List`1" [ "int" ])

[<Fact>]
let ``Dictionary formats with angle brackets`` () =
    Assert.Equal("Dictionary<string, int>",
        formatTypeName "System.Collections.Generic.Dictionary`2" [ "string"; "int" ])

// ---------------------------------------------------------------------------
// Fallback for unknown types
// ---------------------------------------------------------------------------

[<Fact>]
let ``unknown non-generic type uses simple name`` () =
    Assert.Equal("MyClass", formatTypeName "My.Namespace.MyClass" [])

[<Fact>]
let ``unknown generic type strips arity and uses angle-bracket syntax`` () =
    Assert.Equal("MyGeneric<int>",
        formatTypeName "My.Namespace.MyGeneric`1" [ "int" ])

[<Fact>]
let ``type without namespace is kept as-is`` () =
    Assert.Equal("MyType", formatTypeName "MyType" [])

// ---------------------------------------------------------------------------
// Array formatting
// ---------------------------------------------------------------------------

[<Fact>]
let ``1-D array appends [] suffix`` () =
    Assert.Equal("int[]", formatArrayType "int" 1)

[<Fact>]
let ``2-D array appends [,] suffix`` () =
    Assert.Equal("int[,]", formatArrayType "int" 2)

[<Fact>]
let ``3-D array appends [,,] suffix`` () =
    Assert.Equal("string[,,]", formatArrayType "string" 3)

// ---------------------------------------------------------------------------
// Nested generics (simulate recursive formatting)
// ---------------------------------------------------------------------------

[<Fact>]
let ``option of list formats correctly`` () =
    // Simulate: the caller has already formatted the inner list type
    let listOfInt = formatTypeName "Microsoft.FSharp.Collections.FSharpList`1" [ "int" ]
    let optionOfList = formatTypeName "Microsoft.FSharp.Core.FSharpOption`1" [ listOfInt ]
    Assert.Equal("int list option", optionOfList)

[<Fact>]
let ``func taking option returns list`` () =
    let optionInt  = formatTypeName "Microsoft.FSharp.Core.FSharpOption`1" [ "int" ]
    let listString = formatTypeName "Microsoft.FSharp.Collections.FSharpList`1" [ "string" ]
    let funcType   = formatTypeName "Microsoft.FSharp.Core.FSharpFunc`2" [ optionInt; listString ]
    Assert.Equal("int option -> string list", funcType)

// ---------------------------------------------------------------------------
// Anonymous record types
// ---------------------------------------------------------------------------

[<Fact>]
let ``anonymous record with two fields`` () =
    Assert.Equal("{| Age: int; Name: string |}",
        formatAnonymousRecordType false [ ("Age", "int"); ("Name", "string") ])

[<Fact>]
let ``anonymous record with one field`` () =
    Assert.Equal("{| X: float |}",
        formatAnonymousRecordType false [ ("X", "float") ])

[<Fact>]
let ``struct anonymous record with two fields`` () =
    Assert.Equal("struct {| X: float; Y: float |}",
        formatAnonymousRecordType true [ ("X", "float"); ("Y", "float") ])

[<Fact>]
let ``anonymous record with complex field types`` () =
    let intList   = formatTypeName "Microsoft.FSharp.Collections.FSharpList`1" [ "int" ]
    let intOption = formatTypeName "Microsoft.FSharp.Core.FSharpOption`1"       [ "string" ]
    Assert.Equal("{| Items: int list; Tag: string option |}",
        formatAnonymousRecordType false [ ("Items", intList); ("Tag", intOption) ])

[<Fact>]
let ``anonymous record with no fields`` () =
    Assert.Equal("{|  |}", formatAnonymousRecordType false [])
