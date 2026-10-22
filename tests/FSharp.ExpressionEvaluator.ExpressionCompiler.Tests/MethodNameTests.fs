module FSharp.ExpressionEvaluator.Tests.MethodNameTests

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Xunit
open FSharp.ExpressionEvaluator
open FSharp.ExpressionEvaluator.MethodNames
open FSharp.ExpressionEvaluator.Tests.Fixtures

let private pe = new PEReader(File.OpenRead(typeof<Person>.Assembly.Location))
let private md = pe.GetMetadataReader()

let private fixtures = typeof<Person>.DeclaringType

let private ns = "FSharp.ExpressionEvaluator.Tests.Fixtures"

let private all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

let private methodOf (t: Type) (name: string) = t.GetMethods all |> Array.find (fun m -> m.Name = name)

let private namesAndTypes = { Names = true; Types = true; Values = None }

let private frame (m: MethodBase) format = frameName md m.MetadataToken format None

[<Fact>]
let ``curried module function`` () =
    Assert.Equal($"{ns}.add (x: int) (y: int)", frame (methodOf fixtures "add") namesAndTypes)
    Assert.Equal($"{ns}.add x y", frame (methodOf fixtures "add") { namesAndTypes with Types = false })
    Assert.Equal($"{ns}.add (int) (int)", frame (methodOf fixtures "add") { namesAndTypes with Names = false })
    Assert.Equal($"{ns}.add", frame (methodOf fixtures "add") noArguments)

[<Fact>]
let ``tupled and mixed groups`` () =
    Assert.Equal($"{ns}.addTupled (x: int, y: int)", frame (methodOf fixtures "addTupled") namesAndTypes)
    Assert.Equal($"{ns}.mixed (a: int, b: string) (c: float)", frame (methodOf fixtures "mixed") namesAndTypes)
    Assert.Equal($"{ns}.noArgs ()", frame (methodOf fixtures "noArgs") namesAndTypes)

[<Fact>]
let ``F# parameter types`` () =
    Assert.Equal(
        $"{ns}.describe (xs: int list) (f: int -> string) (pair: int * string) (o: int option)",
        frame (methodOf fixtures "describe") namesAndTypes
    )

[<Fact>]
let ``tuple parameters of curried functions`` () =
    let describe = methodOf fixtures "describe"
    let values = Some [ Some "[1]"; Some "<fun>"; Some "1"; Some "\"a\""; Some "None" ]
    Assert.Equal($"{ns}.describe [1] <fun> (1, \"a\") None", frame describe { Names = false; Types = false; Values = values })
    Assert.Equal($"{ns}.describe xs f pair o", frame describe { Names = true; Types = false; Values = None })

[<Fact>]
let ``argument values`` () =
    let values = Some [ Some "1"; Some "2" ]
    Assert.Equal($"{ns}.add (x: int = 1) (y: int = 2)", frame (methodOf fixtures "add") { namesAndTypes with Values = values })
    Assert.Equal($"{ns}.add (x = 1) (y = 2)", frame (methodOf fixtures "add") { Names = true; Types = false; Values = values })
    Assert.Equal($"{ns}.add 1 2", frame (methodOf fixtures "add") { Names = false; Types = false; Values = values })

[<Fact>]
let ``generic methods, with and without the frame's type arguments`` () =
    let identity = methodOf fixtures "identity"
    Assert.Equal($"{ns}.identity<'T> (x: 'T)", frame identity namesAndTypes)
    let int = MetadataTypes.parseTypeName "System.Int32, System.Private.CoreLib"
    Assert.Equal($"{ns}.identity<int> (x: int)", frameName md identity.MetadataToken namesAndTypes (Some [ int.Value ]))

[<Fact>]
let ``generic types`` () =
    let map = methodOf (typedefof<Box<_>>) "Map"
    Assert.Equal($"{ns}.Box<'T>.Map<'U> (f: 'T -> 'U)", frame map namesAndTypes)

[<Fact>]
let ``members, constructors, properties and operators`` () =
    let person = typeof<Person>
    Assert.Equal($"{ns}.Person.Greet (greeting: string, times: int)", frame (methodOf person "Greet") namesAndTypes)
    Assert.Equal($"{ns}.Person.new (name: string)", frame (person.GetConstructors().[0]) namesAndTypes)
    Assert.Equal($"{ns}.Person.Name (get) ()", frame (methodOf person "get_Name") namesAndTypes)
    Assert.Equal($"{ns}.Person.(+) (a: {ns}.Person, b: {ns}.Person)", frame (methodOf person "op_Addition") namesAndTypes)
    Assert.Equal($"{ns}.(+.) (a: int) (b: int)", frame (methodOf fixtures "op_PlusDot") namesAndTypes)

[<Fact>]
let ``module suffix is dropped`` () =
    let personModule = fixtures.GetNestedType "PersonModule"
    Assert.Equal($"{ns}.Person.create (name: string)", frame (methodOf personModule "create") namesAndTypes)

[<Fact>]
let ``closures drop Invoke and keep curried groups`` () =
    let closure = (makeClosure ()).GetType()
    let name = frame (methodOf closure "Invoke") namesAndTypes
    Assert.Matches($@"^{Text.RegularExpressions.Regex.Escape ns}\.makeClosure@\d+ \(x: int\)$", name)
    let unitClosure = (makeUnitClosure ()).GetType()
    Assert.Matches(@"makeUnitClosure@\d+ \(\)$", frame (methodOf unitClosure "Invoke") namesAndTypes)
    Assert.Matches(@"makeUnitClosure@\d+ \(\)$", frame (methodOf unitClosure "Invoke") { Names = true; Types = false; Values = None })
    let curried = (makeCurriedClosure ()).GetType()
    Assert.Matches(@"makeCurriedClosure@\d+ \(x: int\) \(y: int\)$", frame (methodOf curried "Invoke") namesAndTypes)

[<Fact>]
let ``sequence and task state machines`` () =
    let sequence = (numbers ()).GetType()
    Assert.Matches(@"\.numbers@\d+ \(seq\)$", frame (methodOf sequence "GenerateNext") namesAndTypes)

    let stateMachine =
        fixtures.GetNestedTypes all
        |> Array.tryFind (fun t -> t.Name.StartsWith "work@" && t.GetInterface "IAsyncStateMachine" <> null)

    match stateMachine with
    | Some t -> Assert.Matches(@"\.work@\d+ \(state machine\)$", frame (methodOf t "MoveNext") namesAndTypes)
    | None -> () // task { } compiled without a static state machine in this configuration

[<Fact>]
let ``return types`` () =
    Assert.Equal("int", returnTypeName md (methodOf fixtures "add").MetadataToken None)
    Assert.Equal("seq<int>", returnTypeName md (methodOf fixtures "numbers").MetadataToken None)
    Assert.Equal("unit", returnTypeName md (typeof<Person>.GetConstructors().[0]).MetadataToken None)

[<Fact>]
let ``serialized type names`` () =
    let parse s = MetadataTypes.parseTypeName s |> Option.map MetadataTypes.typeName
    Assert.Equal(Some "int", parse "System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e")
    Assert.Equal(Some "string array", parse "System.String[], System.Private.CoreLib")
    Assert.Equal(Some "int list", parse "Microsoft.FSharp.Collections.FSharpList`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0]], FSharp.Core")
    Assert.Equal(
        Some "System.Collections.Generic.Dictionary<string, int option>",
        parse "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Microsoft.FSharp.Core.FSharpOption`1[[System.Int32, System.Private.CoreLib]], FSharp.Core]], System.Private.CoreLib"
    )
    Assert.Equal(Some "Outer.Inner", parse "Outer+Inner, Asm")
