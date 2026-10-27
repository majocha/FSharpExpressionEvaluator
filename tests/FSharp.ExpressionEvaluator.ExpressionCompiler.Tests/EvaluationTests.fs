/// Tests for FSharp.ExpressionEvaluator.Evaluation: expressions are compiled in the
/// context of methods of this assembly (the "debuggee"), then the produced query method is
/// loaded and invoked with argument values standing in for the frame's.
module FSharp.ExpressionEvaluator.Tests.EvaluationTests

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Xunit
open FSharp.ExpressionEvaluator.Evaluation
open Microsoft.VisualStudio.Debugger.Clr

module EvalFixtures =

    let moduleValue = [ 1; 2; 3 ]

    let private secret = 42

    let mutable moduleCounter = 7

    let mutable private privateCounter = 8

    let add (x: int) (y: int) = x + y

    let greet (name: string) = "hello " + name

    let shadowing (moduleValue: int) (set: string) = moduleValue + set.Length

    let withLocals (x: int) =
        let doubled = x * 2
        let label = "n" + string doubled
        doubled + label.Length

    type Counter(start: int) =
        member this.Start = start
        member this.Increment(by: int) = start + by

    type Account(owner: string) =
        let mutable balance = 100

        member this.Deposit(amount: int) =
            balance <- balance + amount
            balance

        member _.Owner = owner

        member this.DepositLater(amount: int) =
            task {
                do! System.Threading.Tasks.Task.Yield()
                balance <- balance + amount
                return this.Owner, balance
            }

    let makeClosure () =
        let captured = 5
        fun (x: int) -> captured + x

    /// A mutable local captured by a closure: a ref cell named `count`, in the frame and in the closure.
    let makeCounter (start: int) =
        let mutable count = start

        let bump (by: int) =
            count <- count + by
            count

        bump

    let pairUp (x: 'a) (n: int) = (x, n)

    let largest (xs: 'T list) = List.max xs

    let tupled (pair: int * string) (z: int) =
        let total = fst pair + z
        total

    let withAnonymousRecord (x: int) =
        let r = {| A = x |}
        r.A + 1

    let countOf (r: {| Name: string; Count: int |}) = r.Count

    let withAnonymousRecords (x: int) =
        let r = {| Name = "anon"; Count = x |}
        let inList = [ r ]
        let inOption = Some r
        let s = struct {| A = x; B = "b" |}
        let nested = {| Inner = r; Tag = 'n' |}
        r.Count + inList.Length + s.A + nested.Inner.Count + Option.count inOption

    let withFSharpTypes (xs: int list) (f: int list -> string option) =
        let names = Some [ "a" ]
        let counts = ValueSome xs
        let total = ref 0
        let units = [ () ]
        let pairs = [ (1, Some 'c') ]
        total.Value + xs.Length + names.Value.Length + counts.Value.Length + units.Length + pairs.Length + (f xs).Value.Length

    let anonymousRecordParameter (r: {| Name: string; Count: int |}) (pairs: {| Key: int |} array) = r.Count + pairs.Length

    let makeAnonymousRecordClosure () =
        let captured = {| Name = "captured"; Count = 5 |}
        fun (x: int) -> captured.Count + x

    type Bag<'T when 'T: equality>(items: 'T list) =
        member _.Has(x: 'T) = List.contains x items

    let countUp (limit: int) =
        seq {
            for i in 1..limit do
                let square = i * i
                yield square + limit
        }

    let taskSum (x: int) =
        task {
            let doubled = x * 2
            do! System.Threading.Tasks.Task.Yield()
            let label = "n" + string doubled
            return doubled + label.Length + x
        }

    type Box<'T>(value: 'T) =
        member this.Value = value
        member this.Get(n: int) = n
        member this.Map<'U>(f: 'T -> 'U) = f value

let private all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static

/// What a debuggee process has loaded: the runtime's implementation assemblies, FSharp.Core and this assembly.
let private loadedAssemblies () =
    let isManaged (path: string) =
        try
            AssemblyName.GetAssemblyName path |> ignore
            true
        with _ ->
            false

    let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)

    [
        yield! Directory.GetFiles(runtimeDir, "*.dll") |> Array.filter isManaged
        typeof<list<int>>.Assembly.Location
        Assembly.GetExecutingAssembly().Location
    ]

/// A module as the debugger sees it in memory: its metadata blob and its F# resources (with their
/// offsets in the resources directory), nothing else.
let private metadataOnly (path: string) =
    use pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead path)
    let md = pe.GetMetadataReader()
    let metadata = pe.GetMetadata().GetContent() |> Seq.toArray
    let resourcesRva = pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress

    let resources =
        [
            for h in md.ManifestResources do
                let r = md.GetManifestResource h

                if r.Implementation.IsNil && (md.GetString r.Name).StartsWith "FSharp" then
                    let mutable blob = pe.GetSectionData(resourcesRva + int r.Offset).GetReader()
                    let length = blob.ReadInt32()
                    int r.Offset, blob.ReadBytes length
        ]

    DebuggeeImage.Metadata(metadata, resources)

/// Where the debuggee's modules claim to live: directories that do not exist on this machine, as
/// for a remote debuggee, so nothing can be read from disk. The shared framework and the app are
/// in different directories, as in a real process.
let private debuggeePath (path: string) =
    let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)
    let dir = if Path.GetDirectoryName path = runtimeDir then @"Z:\debuggee\shared" else @"Z:\debuggee\app"
    Path.Combine(dir, Path.GetFileName path)

/// The debuggee's modules, as metadata only, read from memory as the debugger would. With
/// `framePE` the frame's module (this assembly) is a full image instead.
let private debuggeeModules framePE =
    let self = Assembly.GetExecutingAssembly().Location

    loadedAssemblies ()
    |> List.map (fun path ->
        {
            FileName = debuggeePath path
            Image =
                if framePE && path = self then
                    DebuggeeImage.PE(File.ReadAllBytes path)
                else
                    metadataOnly path
        })

let private session = lazy (EvaluationSession.Create(debuggeeModules false))

let private sessionWithFramePE = lazy (EvaluationSession.Create(debuggeeModules true))

let private fixtures = typeof<EvalFixtures.Counter>.DeclaringType

let private methodOf (t: Type) name = t.GetMethods all |> Array.find (fun m -> m.Name = name)

/// All named locals of a method, from this assembly's portable PDB, as the debugger would read them.
let private pdbLocals (frameMethod: MethodBase) =
    let pdbPath = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".pdb")
    use provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead pdbPath)
    let pdb = provider.GetMetadataReader()
    let handle = MetadataTokens.MethodDefinitionHandle(frameMethod.MetadataToken &&& 0xFFFFFF)

    [
        for scope in pdb.GetLocalScopes handle do
            for v in (pdb.GetLocalScope scope).GetLocalVariables() do
                let local = pdb.GetLocalVariable v
                { Name = pdb.GetString local.Name; Slot = local.Index }
    ]

let private frameOf (frameMethod: MethodBase) =
    {
        AssemblyName = Assembly.GetExecutingAssembly().GetName().Name
        MethodToken = frameMethod.MetadataToken
        LocalSignatureToken =
            match frameMethod.GetMethodBody() with
            | null -> 0
            | body -> body.LocalSignatureMetadataToken
        Opens = []
        Locals = pdbLocals frameMethod
        Aliases = []
        // The stand-in IntrinsicMethods is defined in this assembly.
        IntrinsicsAssembly = Assembly.GetExecutingAssembly().FullName
    }

let private compileWith localsAsParameters (frameMethod: MethodBase) (text: string) =
    session.Value.CompileExpression(frameOf frameMethod, text, localsAsParameters = localsAsParameters)

let private compile frameMethod text = compileWith true frameMethod text

let private run (compiled: Result<CompiledExpression, EvaluationDiagnostic list>) (args: obj list) (text: string) =
    match compiled with
    | Ok compiled ->
        let query = Assembly.Load(compiled.Assembly).GetType(compiled.TypeName).GetMethod(compiled.MethodName, all)
        query.Invoke(null, Array.ofList args), compiled.ResultType
    | Error diagnostics -> failwithf "%s: %A" text diagnostics

/// Compiles `text` in the frame of `frameMethod`, runs it with `args` as the frame's this/parameters.
let private evaluate (frameMethod: MethodBase) (args: obj list) (text: string) =
    run (compile frameMethod text) args text

/// As `evaluate`, in a generic frame: the query class and method are instantiated as the frame's.
let private evaluateGeneric (classArgs: Type list) (methodArgs: Type list) (frameMethod: MethodBase) (args: obj list) (text: string) =
    match compile frameMethod text with
    | Ok compiled ->
        let queryType = Assembly.Load(compiled.Assembly).GetType(compiled.TypeName)

        let queryType =
            if classArgs.IsEmpty then queryType else queryType.MakeGenericType(Array.ofList classArgs)

        let query = queryType.GetMethod(compiled.MethodName, all)

        let query =
            if methodArgs.IsEmpty then query else query.MakeGenericMethod(Array.ofList methodArgs)

        query.Invoke(null, Array.ofList args), compiled.ResultType
    | Error diagnostics -> failwithf "%s: %A" text diagnostics

/// As `evaluate`, with the debugger's aliases in scope.
let private evaluateWithAliases aliases (frameMethod: MethodBase) (args: obj list) (text: string) =
    let frame = { frameOf frameMethod with Aliases = aliases }
    run (session.Value.CompileExpression(frame, text, localsAsParameters = true)) args text

[<Fact>]
let ``the session reads every module from memory`` () =
    Assert.All(debuggeeModules false, fun m -> Assert.False(File.Exists m.FileName))
    Assert.Equal((box 3, "int"), evaluate (methodOf fixtures "add") [ 1; 2 ] "List.length moduleValue")

[<Fact>]
let ``constant expression`` () =
    Assert.Equal((box 3, "int"), evaluate (methodOf fixtures "add") [ 1; 2 ] "1 + 2")

[<Fact>]
let ``parameters of the frame`` () =
    Assert.Equal((box 7, "int"), evaluate (methodOf fixtures "add") [ 3; 4 ] "x + y")
    Assert.Equal((box "hello you!", "string"), evaluate (methodOf fixtures "greet") [ "you" ] "greet name + \"!\"")

[<Fact>]
let ``module values and FSharp.Core functions`` () =
    Assert.Equal((box 3, "int"), evaluate (methodOf fixtures "add") [ 0; 0 ] "moduleValue |> List.length")
    Assert.Equal((box [ 2; 4; 6 ], "int list"), evaluate (methodOf fixtures "add") [ 2; 0 ] "moduleValue |> List.map (fun i -> i * x)")

[<Fact>]
let ``private module values are accessible from the frame's module`` () =
    Assert.Equal((box 42, "int"), evaluate (methodOf fixtures "add") [ 0; 0 ] "secret")

[<Fact>]
let ``mutable module values`` () =
    Assert.Equal((box 7, "int"), evaluate (methodOf fixtures "add") [ 0; 0 ] "moduleCounter")
    Assert.Equal((box 8, "int"), evaluate (methodOf fixtures "add") [ 0; 0 ] "privateCounter")

/// The top-level code of a file, in the `<StartupCode$…>` type named `typeName`.
let private startupFrame typeName =
    typeof<EvalFixtures.Counter>.Assembly.GetTypes()
    |> Array.find (fun t -> t.FullName.EndsWith("$" + typeName))
    |> _.TypeInitializer

[<Fact>]
let ``top-level code sees its module's mutable values`` () =
    let frame = startupFrame "FSharp.ExpressionEvaluator.Tests.StartupFixture"
    Assert.Equal((box 9, "int"), evaluate frame [] "startupCounter")
    Assert.Equal((box 10, "int"), evaluate frame [] "hiddenCounter")

[<Fact>]
let ``top-level code of a namespace sees the mutable values of the modules it sets`` () =
    let frame = startupFrame "NamespaceStartupFixture"
    Assert.Equal((box 11, "int"), evaluate frame [] "firstCounter")
    Assert.Equal((box 13, "int"), evaluate frame [] "nestedCounter")
    Assert.Equal((box 12, "int"), evaluate frame [] "secondCounter")

    match compile frame "helper ()" with
    | Ok _ -> failwith "Helpers sets no values, so it should not be opened"
    | Error _ -> ()

/// The assemblies a query references, by name, and the types it defines.
let private referencesAndTypes (assembly: byte[]) =
    use pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(assembly))
    let md = pe.GetMetadataReader()

    [ for h in md.AssemblyReferences -> md.GetString (md.GetAssemblyReference h).Name ],
    [ for h in md.TypeDefinitions -> md.GetString (md.GetTypeDefinition h).Name ]

/// A .NET Framework process: mscorlib, with the System.Runtime and netstandard facades only
/// referenced, and the netstandard2.0 FSharp.Core. Its frame is List.length in FSharp.Core.
let private netfxSession =
    lazy
        (let framework =
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.Windows, @"Microsoft.NET\Framework64\v4.0.30319")

         let fsharpCore = Path.Combine(AppContext.BaseDirectory, "netfx", "FSharp.Core.dll")

         let modules =
             [
                 for name in [ "mscorlib"; "System"; "System.Core"; "System.Numerics"; "System.Runtime"; "netstandard" ] do
                     Path.Combine(framework, name + ".dll"), @"Z:\debuggee\framework"

                 fsharpCore, @"Z:\debuggee\app"
             ]
             |> List.map (fun (path, dir) ->
                 {
                     FileName = Path.Combine(dir, Path.GetFileName path)
                     Image = metadataOnly path
                 })

         let lengthToken =
             use pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead fsharpCore)
             let md = pe.GetMetadataReader()

             md.TypeDefinitions
             |> Seq.map md.GetTypeDefinition
             |> Seq.filter (fun t -> md.GetString t.Name = "ListModule")
             |> Seq.collect _.GetMethods()
             |> Seq.find (fun h -> md.GetString (md.GetMethodDefinition h).Name = "Length")
             |> fun h -> MetadataTokens.GetToken(MethodDefinitionHandle.op_Implicit h: EntityHandle)

         let frame =
             {
                 AssemblyName = "FSharp.Core"
                 MethodToken = lengthToken
                 LocalSignatureToken = 0
                 Opens = []
                 Locals = []
                 Aliases = []
                 IntrinsicsAssembly = ""
             }

         EvaluationSession.Create modules, frame)

let private compileNetfx text =
    let session, frame = netfxSession.Value

    match session.CompileExpression(frame, text) with
    | Ok compiled -> referencesAndTypes compiled.Assembly
    | Error diagnostics -> failwithf "%s: %A" text diagnostics

[<Fact>]
let ``queries in a .NET Framework debuggee reference mscorlib, not System.Runtime`` () =
    // The query couldn't run there if it referenced System.Runtime, which such a process never loads.
    let references, _ = compileNetfx "[ 1; 2 ] |> List.map string |> String.concat \",\""
    Assert.Contains("mscorlib", references)
    Assert.DoesNotContain("System.Runtime", references)

[<Fact>]
let ``queries define the attributes .NET Framework lacks`` () =
    // An anonymous record's class carries DynamicDependencyAttribute, which mscorlib doesn't have.
    let _, types = compileNetfx "{| Name = \"a\"; Count = 1 |}"
    Assert.Contains("DynamicDependencyAttribute", types)

[<Fact>]
let ``this and parameters of an instance member`` () =
    let increment = methodOf typeof<EvalFixtures.Counter> "Increment"
    let counter = EvalFixtures.Counter 10
    Assert.Equal((box 15, "int"), evaluate increment [ counter; 2 ] "this.Start + by + 3")

[<Fact>]
let ``errors are reported with F# diagnostics`` () =
    match compile (methodOf fixtures "add") "undefinedThing + x" with
    | Ok _ -> failwith "expected an error"
    | Error diagnostics -> Assert.Contains(diagnostics, fun d -> d.Number = 39)

[<Fact>]
let ``locals of the frame`` () =
    let withLocals = methodOf fixtures "withLocals"
    let locals = (frameOf withLocals).Locals |> List.map _.Name
    Assert.Equal<string list>([ "doubled"; "label" ], locals)
    // With localsAsParameters the locals follow the parameters: x, doubled, label.
    Assert.Equal((box 11, "int"), evaluate withLocals [ 3; 6; "n6" ] "doubled + label.Length + x")

[<Fact>]
let ``the query method starts its locals with the frame's local signature`` () =
    let withLocals = methodOf fixtures "withLocals"

    match compileWith false withLocals "doubled + x" with
    | Error diagnostics -> failwithf "%A" diagnostics
    | Ok compiled ->
        let query = Assembly.Load(compiled.Assembly).GetType(compiled.TypeName).GetMethod(compiled.MethodName, all)
        let frameLocals = withLocals.GetMethodBody().LocalVariables |> Seq.map _.LocalType.FullName |> List.ofSeq
        let queryLocals = query.GetMethodBody().LocalVariables |> Seq.map _.LocalType.FullName |> List.ofSeq

        Assert.Equal<string list>(frameLocals, List.truncate frameLocals.Length queryLocals)
        Assert.Equal(1, query.GetParameters().Length)
        // Invoked directly the frame's locals are zero-initialized: doubled = 0.
        Assert.Equal(box 5, query.Invoke(null, [| box 5 |]))

[<Fact>]
let ``with a full image of the frame's module the local signature comes from the method body`` () =
    let withLocals = methodOf fixtures "withLocals"
    let frame = { frameOf withLocals with LocalSignatureToken = 0 }

    match sessionWithFramePE.Value.CompileExpression(frame, "doubled + label.Length + x", localsAsParameters = true) with
    | Error diagnostics -> failwithf "%A" diagnostics
    | Ok compiled ->
        let query = Assembly.Load(compiled.Assembly).GetType(compiled.TypeName).GetMethod(compiled.MethodName, all)
        Assert.Equal(box 11, query.Invoke(null, [| box 3; box 6; box "n6" |]))

[<Fact>]
let ``captured values of a closure frame`` () =
    let closure = EvalFixtures.makeClosure ()
    let invoke = closure.GetType().GetMethod("Invoke", [| typeof<int> |])
    Assert.Equal((box 6, "int"), evaluate invoke [ closure; 1 ] "captured + x")
    Assert.Equal((box 15, "int"), evaluate invoke [ closure; 1 ] "([ 1 .. x ] |> List.map (fun i -> i + captured) |> List.sum) + 9")

[<Fact>]
let ``let fields of a class member frame`` () =
    let deposit = methodOf typeof<EvalFixtures.Account> "Deposit"
    let account = EvalFixtures.Account "ann"
    Assert.Equal((box 110, "int"), evaluate deposit [ account; 10 ] "balance + amount")
    Assert.Equal((box "ann!", "string"), evaluate deposit [ account; 10 ] "owner + \"!\"")

[<Fact>]
let ``frame values shadow the opened modules`` () =
    // `moduleValue` of the frame's own module, `set` of FSharp.Core
    let shadowing = methodOf fixtures "shadowing"
    Assert.Equal((box 7, "int"), evaluate shadowing [ 4; "abc" ] "moduleValue + set.Length")

[<Fact>]
let ``generic results are fixed to obj`` () =
    let add = methodOf fixtures "add"
    let value, ty = evaluate add [ 0; 0 ] "[]"
    Assert.Equal("obj list", ty)
    Assert.Equal<obj list>([], value :?> obj list)
    Assert.Equal((null, "obj option"), evaluate add [ 0; 0 ] "None")

[<Fact>]
let ``the exception alias`` () =
    IntrinsicMethods.Exception <- InvalidOperationException "boom"

    let exceptionAlias =
        {
            Name = "$exception"
            Kind = AliasKind.Exception
            TypeName = typeof<InvalidOperationException>.AssemblyQualifiedName
        }

    let value, ty =
        evaluateWithAliases [ exceptionAlias ] (methodOf fixtures "add") [ 1; 2 ] "$exception.Message + string x"

    Assert.Equal(box "boom1", value)
    Assert.Equal("string", ty)

    let value, ty = evaluateWithAliases [ exceptionAlias ] (methodOf fixtures "add") [ 1; 2 ] "$exception"
    Assert.IsType<InvalidOperationException>(value) |> ignore
    Assert.Equal("System.InvalidOperationException", ty)

[<Fact>]
let ``return value aliases`` () =
    IntrinsicMethods.ReturnValues <- [ box 10; box "ignored"; box 30 ]

    let returnValue name =
        {
            Name = name
            Kind = AliasKind.ReturnValue
            TypeName = "System.Int32"
        }

    Assert.Equal(
        (box 41, "int"),
        evaluateWithAliases
            [ returnValue "$ReturnValue"; returnValue "$ReturnValue2" ]
            (methodOf fixtures "add")
            [ 1; 2 ]
            "$ReturnValue + $ReturnValue2 + x"
    )

[<Fact>]
let ``object ids and user variables`` () =
    IntrinsicMethods.Variables["$1"] <- box [ 1; 2; 3 ]
    IntrinsicMethods.Variables["total"] <- box 5

    let aliases =
        [
            {
                Name = "$1"
                Kind = AliasKind.ObjectId
                TypeName = typeof<int list>.AssemblyQualifiedName
            }
            {
                Name = "total"
                Kind = AliasKind.Variable
                TypeName = typeof<int>.FullName
            }
        ]

    let add = methodOf fixtures "add"
    Assert.Equal((box 11, "int"), evaluateWithAliases aliases add [ 1; 2 ] "List.sum $1 + total")
    // `$1` inside literals and comments is left alone
    Assert.Equal((box "$1 3", "string"), evaluateWithAliases aliases add [ 1; 2 ] "\"$1 \" + string $1.Length // $1")

[<Fact>]
let ``an alias of a type the session can't find reads as obj`` () =
    IntrinsicMethods.Variables["$2"] <- box "x"

    let alias =
        {
            Name = "$2"
            Kind = AliasKind.ObjectId
            TypeName = "No.Such.Type, NoSuchAssembly"
        }

    Assert.Equal((box "x", "obj"), evaluateWithAliases [ alias ] (methodOf fixtures "add") [ 1; 2 ] "$2")

[<Fact>]
let ``generic method frames`` () =
    let pairUp = methodOf fixtures "pairUp"
    let evaluate = evaluateGeneric [] [ typeof<string> ] pairUp [ "hi"; 2 ]
    Assert.Equal((box ("hi", 3), "'a * int"), evaluate "(x, n + 1)")
    Assert.Equal((box "String", "string"), evaluate "typeof<'a>.Name")

[<Fact>]
let ``generic class frames`` () =
    let boxType = typedefof<EvalFixtures.Box<_>>
    let get = methodOf boxType "Get"
    let evaluate = evaluateGeneric [ typeof<string> ] [] get [ EvalFixtures.Box "v"; 2 ]
    Assert.Equal((box ("v", 2), "'T * int"), evaluate "(value, n)")
    Assert.Equal((box "v!", "string"), evaluate "string this.Value + \"!\"")
    Assert.Equal((box [ "v"; "v" ], "'T list"), evaluate "[ 1..n ] |> List.map (fun _ -> value)")

[<Fact>]
let ``generic methods of generic classes`` () =
    let map = methodOf typedefof<EvalFixtures.Box<_>> "Map"
    let f = fun (s: string) -> s.Length

    Assert.Equal(
        (box (1, "v"), "'U * 'T"),
        evaluateGeneric [ typeof<string> ] [ typeof<int> ] map [ EvalFixtures.Box "v"; f ] "(f value, value)"
    )

/// The state machine of a `task` in `owner.name` (a generated struct), boxed, with fields set.
let private stateMachine (owner: Type) (name: string) (fields: (string * obj) list) =
    let t =
        owner.GetNestedTypes all
        |> Array.find (fun t -> t.IsValueType && t.Name.StartsWith(name + "@"))

    let machine = Activator.CreateInstance t

    for field, value in fields do
        t.GetField(field, all).SetValue(machine, value)

    t.GetMethod("MoveNext", all), machine

/// Default values for the frame's named locals, which follow the parameters with localsAsParameters.
let private localDefaults (frameMethod: MethodBase) =
    let types = frameMethod.GetMethodBody().LocalVariables

    (frameOf frameMethod).Locals
    |> List.map (fun l ->
        let ty = types[l.Slot].LocalType
        if ty.IsValueType then Activator.CreateInstance ty else null)

[<Fact>]
let ``hoisted variables of a task state machine`` () =
    let moveNext, machine =
        stateMachine fixtures "taskSum" [ "x", box 3; "doubled", box 6 ]

    // `label` is a local of MoveNext; `x` and `doubled` are fields of the state machine.
    Assert.Equal<string list>([ "label" ], (frameOf moveNext).Locals |> List.map _.Name)
    Assert.Equal((box 11, "int"), evaluate moveNext [ machine; "n6" ] "doubled + label.Length + x")

    // The state machine's plumbing is not visible.
    for plumbing in [ "ResumptionPoint"; "Data"; "awaiter"; "this" ] do
        match compile moveNext plumbing with
        | Ok _ -> failwithf "%s should not be in scope" plumbing
        | Error _ -> ()

[<Fact>]
let ``a task in a class member sees the member's this and the class's fields`` () =
    let account = EvalFixtures.Account "ann"

    let moveNext, machine =
        stateMachine fixtures "DepositLater" [ "this", box account; "amount", box 5 ]

    let args = machine :: localDefaults moveNext

    Assert.Equal(
        (box "ann 105 ann", "string"),
        evaluate moveNext args "owner + \" \" + string (balance + amount) + \" \" + this.Owner"
    )

[<Fact>]
let ``variables of a seq state machine`` () =
    let generator =
        fixtures.GetNestedTypes all |> Array.find (fun t -> t.Name.StartsWith "countUp@")

    let machine = Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject generator
    generator.GetField("limit", all).SetValue(machine, 3)
    let generateNext = generator.GetMethod("GenerateNext", all)

    // GenerateNext(this, next), then its named locals
    let locals =
        (frameOf generateNext).Locals
        |> List.map (fun l ->
            match l.Name with
            | "i" -> box 2
            | "square" -> box 4
            | _ -> null)

    Assert.Equal((box 9, "int"), evaluate generateNext (machine :: null :: locals) "limit + i + square")

    for plumbing in [ "pc"; "current"; "redirectTo"; "this" ] do
        match compile generateNext plumbing with
        | Ok _ -> failwithf "%s should not be in scope" plumbing
        | Error _ -> ()


[<Fact>]
let ``trailing format specifiers are split off`` () =
    let add = methodOf fixtures "add"

    let specifiers text =
        match compile add text with
        | Ok compiled -> compiled.ResultType, compiled.FormatSpecifiers
        | Error diagnostics -> failwithf "%s: %A" text diagnostics

    Assert.Equal(("int", [ "h" ]), specifiers "x, h")
    Assert.Equal(("int", [ "h"; "nq" ]), specifiers "x + y , h,nq")
    Assert.Equal(("string", [ "nq" ]), specifiers "\"a, h\", nq")
    Assert.Equal(("int list", [ "view(simple)" ]), specifiers "[ x, y ] |> List.map fst, view(simple)")
    // Not specifiers: a tuple, and a tuple in parentheses
    Assert.Equal(("int * int", []), specifiers "x, y")
    Assert.Equal(("int * int", []), specifiers "(x, y)")
    Assert.Equal((box (3, 4), "int * int"), evaluate add [ 3; 4 ] "x, y")

[<Fact>]
let ``result properties`` () =
    let properties frameMethod text =
        match compile frameMethod text with
        | Ok c -> c.PotentialSideEffect, c.ReadOnlyResult, c.BoolResult
        | Error diagnostics -> failwithf "%s: %A" text diagnostics

    let add = methodOf fixtures "add"
    let increment = methodOf typeof<EvalFixtures.Counter> "Increment"

    // (side effects, read-only, bool)
    Assert.Equal((false, false, false), properties add "x")
    Assert.Equal((false, true, false), properties add "x + y")
    Assert.Equal((false, true, true), properties add "x > y && not (y = 0)")
    Assert.Equal((false, true, false), properties add "string x + string (moduleValue |> List.length)")
    Assert.Equal((false, true, false), properties add "fst (x, y)")
    Assert.Equal((false, true, false), properties increment "this.Start + by")
    Assert.Equal((true, true, false), properties add "greet \"you\"")
    Assert.Equal((true, true, false), properties add "moduleValue |> List.map (fun i -> i + x)")
    Assert.Equal((true, true, false), properties add "System.Text.StringBuilder()")
    Assert.Equal((true, true, false), properties add "\"abc\".ToUpper()")
    Assert.Equal((false, true, false), properties add "\"abc\".Length")

let private assign localsAsParameters (frameMethod: MethodBase) target value =
    match session.Value.CompileAssignment(frameOf frameMethod, target, value, localsAsParameters = localsAsParameters) with
    | Ok compiled -> compiled
    | Error diagnostics -> failwithf "%s <- %s: %A" target value diagnostics

let private queryMethod (compiled: CompiledExpression) =
    Assembly.Load(compiled.Assembly).GetType(compiled.TypeName).GetMethod(compiled.MethodName, all)

[<Fact>]
let ``assignment to fields of this`` () =
    let deposit = methodOf typeof<EvalFixtures.Account> "Deposit"
    let account = EvalFixtures.Account "ann"
    let compiled = assign true deposit "balance" "amount * 7"
    Assert.True compiled.PotentialSideEffect
    Assert.Equal("unit", compiled.ResultType)
    (queryMethod compiled).Invoke(null, [| box account; box 6 |]) |> ignore
    Assert.Equal(42, account.Deposit 0)

[<Fact>]
let ``assignment to a hoisted variable of a state machine`` () =
    let moveNext, machine =
        stateMachine fixtures "taskSum" [ "x", box 3; "doubled", box 6 ]

    let args = [| machine; box "n6" |]
    (queryMethod (assign true moveNext "doubled" "x + label.Length")).Invoke(null, args) |> ignore
    // The state machine is passed by reference: the boxed copy in args is updated.
    Assert.Equal(box 5, args[0].GetType().GetField("doubled", all).GetValue args[0])

[<Fact>]
let ``assignment to parameters and locals stores to the frame's slots`` () =
    let hasOpcodes (code: byte list) (compiled: CompiledExpression) =
        let il = (queryMethod compiled).GetMethodBody().GetILAsByteArray() |> List.ofArray
        il |> List.windowed code.Length |> List.contains code

    // starg.s 0: the parameter x
    Assert.True(hasOpcodes [ 0x10uy; 0x00uy ] (assign false (methodOf fixtures "add") "x" "y * 10"))

    // stloc to the slot of `doubled` in withLocals (stloc.0-3 or stloc.s)
    let withLocals = methodOf fixtures "withLocals"

    let slot =
        (frameOf withLocals).Locals |> List.find (fun l -> l.Name = "doubled") |> _.Slot

    let stloc =
        if slot <= 3 then [ 0x0Auy + byte slot ] else [ 0x13uy; byte slot ]

    Assert.True(hasOpcodes stloc (assign false withLocals "doubled" "x + 1"))

[<Fact>]
let ``frame values are not mutable in expressions`` () =
    match compile (methodOf fixtures "add") "x <- 1" with
    | Ok _ -> failwith "expected an error"
    | Error diagnostics -> Assert.Contains(diagnostics, fun d -> d.Number = 27)

[<Fact>]
let ``diagnostics are positioned in the expression text`` () =
    let rangeOf (result: Result<CompiledExpression, EvaluationDiagnostic list>) =
        match result with
        | Ok _ -> failwith "expected an error"
        | Error diagnostics ->
            let d = diagnostics |> List.find (fun d -> d.Number = 39)
            let r = d.Range.Value
            (r.StartLine, r.StartColumn, r.EndLine, r.EndColumn)

    let add = methodOf fixtures "add"
    Assert.Equal((1, 4, 1, 18), rangeOf (compile add "x + undefinedThing"))
    Assert.Equal((2, 4, 2, 18), rangeOf (compile add "let a = 1\r\na + undefinedThing"))

    // After `$1`, which is quoted to ``$1`` before parsing
    let objectId =
        {
            Name = "$1"
            Kind = AliasKind.ObjectId
            TypeName = "System.Int32"
        }

    let frame = { frameOf add with Aliases = [ objectId ] }
    Assert.Equal((1, 5, 1, 19), rangeOf (session.Value.CompileExpression(frame, "$1 + undefinedThing")))

[<Fact>]
let ``F# constraints of generic frames`` () =
    // 'T: comparison, which IL does not record
    let largest = methodOf fixtures "largest"

    Assert.Equal(
        (box "c", "'T"),
        evaluateGeneric [] [ typeof<string> ] largest [ [ "a"; "c"; "b" ] ] "List.max xs"
    )

    // 'T: equality on a class
    let has = methodOf typedefof<EvalFixtures.Bag<_>> "Has"

    Assert.Equal(
        (box true, "bool"),
        evaluateGeneric [ typeof<int> ] [] has [ EvalFixtures.Bag [ 1; 2 ]; 2 ] "List.contains x items && x = x"
    )

[<Fact>]
let ``anonymous record locals`` () =
    let frameMethod = methodOf fixtures "withAnonymousRecord"
    Assert.Equal((box 2, "int"), evaluate frameMethod [ 1; {| A = 1 |} ] "r.A + 1")

    let frameMethod = methodOf fixtures "withAnonymousRecords"
    let r = {| Name = "anon"; Count = 3 |}

    let args =
        [ box 3; box r; box [ r ]; box (Some r); box (struct {| A = 3; B = "b" |}); box {| Inner = r; Tag = 'n' |} ]

    let eval text = evaluate frameMethod args text

    Assert.Equal((box "anon", "string"), eval "r.Name")
    Assert.Equal((box 4, "int"), eval "r.Count + 1")
    Assert.Equal((box 3, "int"), eval "countOf r")
    // Copy and update: with no type to aim at, the copy is of the query's own anonymous record type
    // (as in any other assembly), otherwise of the frame's.
    let copy, copyType = eval "{| r with Count = 4 |}"
    Assert.Equal(("{| Count: int; Name: string |}", string {| Name = "anon"; Count = 4 |}), (copyType, string copy))
    Assert.Equal((box 4, "int"), eval "countOf {| r with Count = 4 |}")
    Assert.Equal((box 5, "int"), eval "countOf {| Name = r.Name; Count = 5 |}")
    Assert.Equal((box true, "bool"), eval "r = {| Name = \"anon\"; Count = 3 |}")
    Assert.Equal((box r, "{| Count: int; Name: string |}"), eval "r")

    // Inside generic types, struct anonymous records, anonymous records as field types
    Assert.Equal((box "anon", "string"), eval "inList.Head.Name")
    Assert.Equal((box 3, "int"), eval "inOption.Value.Count")
    Assert.Equal((box "b3", "string"), eval "s.B + string s.A")
    Assert.Equal((box "anon", "string"), eval "nested.Inner.Name")
    Assert.Equal((box 'n', "char"), eval "nested.Tag")
    Assert.Equal((box [ 3 ], "int list"), eval "inList |> List.map (fun a -> a.Count)")

    // An anonymous record the frame's assembly has no class for is generated in the query
    Assert.Equal((box "anon!", "string"), eval "{| Shout = r.Name + \"!\" |}.Shout")

[<Fact>]
let ``anonymous record parameters and captured values`` () =
    let frameMethod = methodOf fixtures "anonymousRecordParameter"
    let r = {| Name = "p"; Count = 2 |}
    Assert.Equal((box "p2", "string"), evaluate frameMethod [ r; [| {| Key = 7 |} |] ] "r.Name + string r.Count")
    Assert.Equal((box 7, "int"), evaluate frameMethod [ r; [| {| Key = 7 |} |] ] "pairs[0].Key")

    let closure = EvalFixtures.makeAnonymousRecordClosure ()
    let invoke = closure.GetType().GetMethod("Invoke", [| typeof<int> |])
    Assert.Equal((box "captured", "string"), evaluate invoke [ closure; 1 ] "captured.Name")
    Assert.Equal((box 6, "int"), evaluate invoke [ closure; 1 ] "captured.Count + x")

[<Fact>]
let ``list and array expressions don't use collector structs`` () =
    // The debugger's IL interpreter passes a struct local to a call by copy: a ListCollector filled
    // through `ldloca` would stay empty there.
    let add = methodOf fixtures "add"

    for text, expected in
        [
            "[ x .. y ]", box [ 1; 2; 3 ]
            "[| for i in 1 .. y -> i * x |]", box [| 1; 2; 3 |]
            "[ for i in x .. y -> [| x .. i |] ]", box [ [| 1 |]; [| 1; 2 |]; [| 1; 2; 3 |] ]
        ] do
        match compileWith false add text with
        | Error diagnostics -> failwithf "%A" diagnostics
        | Ok compiled ->
            let referencesCollector =
                use pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(compiled.Assembly))
                let md = pe.GetMetadataReader()

                md.TypeReferences
                |> Seq.exists (fun h -> (md.GetString (md.GetTypeReference h).Name).Contains "Collector")

            Assert.False(referencesCollector, text)
            Assert.Equal(expected, (queryMethod compiled).Invoke(null, [| box 1; box 3 |]))

/// The Locals window's variables of a frame, with their values read by running each query method.
let private localsOf (frame: FrameContext) (frameMethod: MethodBase) (args: obj list) argumentsOnly =
    match session.Value.CompileLocals(frame, argumentsOnly, localsAsParameters = true) with
    | Error diagnostics -> failwithf "%A" diagnostics
    | Ok compiled ->
        let queryType = Assembly.Load(compiled.Assembly).GetType(compiled.TypeName)

        [
            for local in compiled.Locals ->
                let value =
                    queryType.GetMethod(local.MethodName, all).Invoke(null, Array.ofList args)

                local.Name, value, local.ResultType, local.ReadOnlyResult
        ]

[<Fact>]
let ``locals of a function: parameters, then locals`` () =
    let withLocals = methodOf fixtures "withLocals"

    Assert.Equal<(string * obj * string * bool) list>(
        [ "x", box 3, "int", false; "doubled", box 6, "int", false; "label", box "n6", "string", false ],
        localsOf (frameOf withLocals) withLocals [ 3; 6; "n6" ] false
    )

    // Only the parameters, for the call stack's argument values
    Assert.Equal<string list>([ "x" ], localsOf (frameOf withLocals) withLocals [ 3; 6; "n6" ] true |> List.map (fun (n, _, _, _) -> n))

[<Fact>]
let ``locals show a tuple parameter as the tuple`` () =
    let tupled = methodOf fixtures "tupled"
    let frame = frameOf tupled
    // pair_0, pair_1 (parameters), z, then the locals pair and total
    let names = localsOf frame tupled [ 1; "a"; 2; (1, "a"); 3 ] false |> List.map (fun (n, _, _, _) -> n)
    Assert.Equal<string list>([ "z"; "pair"; "total" ], names)

[<Fact>]
let ``locals of a member: this first, the class's fields under it`` () =
    let deposit = methodOf typeof<EvalFixtures.Account> "Deposit"
    let names = localsOf (frameOf deposit) deposit [ EvalFixtures.Account "ann"; 5 ] false |> List.map (fun (n, _, _, _) -> n)
    Assert.Equal<string list>([ "this"; "amount" ], names)

[<Fact>]
let ``locals of a closure and a state machine are flattened`` () =
    let closure = EvalFixtures.makeClosure ()
    let invoke = closure.GetType().GetMethod("Invoke", [| typeof<int> |])

    Assert.Equal<(string * obj * string * bool) list>(
        [ "x", box 1, "int", false; "captured", box 5, "int", false ],
        localsOf (frameOf invoke) invoke [ closure; 1 ] false
    )

    let moveNext, machine = stateMachine fixtures "taskSum" [ "x", box 3; "doubled", box 6 ]
    let names = localsOf (frameOf moveNext) moveNext [ machine; "n6" ] false |> List.map (fun (n, _, _, _) -> n)
    Assert.Equal<string list>([ "x"; "doubled"; "label" ], names)

    let moveNext, machine =
        stateMachine fixtures "DepositLater" [ "this", box (EvalFixtures.Account "ann"); "amount", box 5 ]

    let names =
        localsOf (frameOf moveNext) moveNext (machine :: localDefaults moveNext) false
        |> List.map (fun (n, _, _, _) -> n)

    Assert.Equal<string list>([ "this"; "amount" ], List.truncate 2 names)

[<Fact>]
let ``a captured mutable local reads and writes as its contents`` () =
    // In the closure: its field `count`
    let bump = EvalFixtures.makeCounter 10
    let invoke = bump.GetType().GetMethod("Invoke", [| typeof<int> |])
    Assert.Equal((box 11, "int"), evaluate invoke [ bump; 1 ] "count + by")
    // As a ref cell when it doesn't check as its contents
    Assert.Equal((box 10, "int"), evaluate invoke [ bump; 1 ] "count.Value")
    (queryMethod (assign true invoke "count" "by * 42")).Invoke(null, [| box bump; box 1 |]) |> ignore
    Assert.Equal(42, bump 0)

    Assert.Equal<(string * obj * string * bool) list>(
        [ "by", box 1, "int", false; "count", box 42, "int", false ],
        localsOf (frameOf invoke) invoke [ bump; 1 ] false
    )

    // In the function that declares it: its local `count`
    let makeCounter = methodOf fixtures "makeCounter"
    Assert.Equal<string list>([ "count"; "bump" ], (frameOf makeCounter).Locals |> List.map _.Name)
    let args = [ box 10; box (ref 7); null ]
    Assert.Equal((box 8, "int"), evaluate makeCounter args "count + 1")
    Assert.Equal((box (ref 7), "int ref"), evaluate makeCounter args "ref count")

    Assert.Equal<(string * obj * string * bool) list>(
        [ "start", box 10, "int", false; "count", box 7, "int", false ],
        localsOf (frameOf makeCounter) makeCounter args false |> List.truncate 2
    )

    let cell = ref 7
    (queryMethod (assign true makeCounter "count" "start + 1")).Invoke(null, [| box 10; box cell; null |]) |> ignore
    Assert.Equal(11, cell.Value)

[<Fact>]
let ``locals start with the debugger's aliases`` () =
    IntrinsicMethods.Exception <- InvalidOperationException "boom"
    let add = methodOf fixtures "add"

    let frame =
        { frameOf add with
            Aliases =
                [
                    {
                        Name = "$exception"
                        Kind = AliasKind.Exception
                        TypeName = typeof<InvalidOperationException>.AssemblyQualifiedName
                    }
                ]
        }

    let locals = localsOf frame add [ 1; 2 ] false
    Assert.Equal<string list>([ "$exception"; "x"; "y" ], locals |> List.map (fun (n, _, _, _) -> n))
    let _, value, ty, _ = List.head locals
    Assert.Equal("boom", (value :?> exn).Message)
    Assert.Equal("System.InvalidOperationException", ty)


/// Timings for docs/DESIGN.md §6.9, written to the file FSHARP_EE_PERF names (nothing runs without
/// it). Cold numbers need the test to run alone, in a process of its own:
/// `dotnet test --filter "FullyQualifiedName~timings"`.
[<Fact>]
let ``the compiler warms up over the runtime's own assemblies`` () =
    let modules, compiled = EvaluationSession.WarmUpCompiler()
    Assert.True(modules > 2, $"{modules} modules")
    Assert.True(compiled)

[<Fact>]
let ``timings of the session and of queries`` () =
    match Environment.GetEnvironmentVariable "FSHARP_EE_PERF" with
    | null
    | "" -> ()
    | path ->
        let lines = ResizeArray<string>()

        let time name f =
            let sw = Diagnostics.Stopwatch.StartNew()
            let value = f ()
            lines.Add $"{name}: {sw.Elapsed.TotalMilliseconds:F1} ms"
            value

        let median name n f =
            let times =
                [
                    for _ in 1..n ->
                        let sw = Diagnostics.Stopwatch.StartNew()
                        f () |> ignore
                        sw.Elapsed.TotalMilliseconds
                ]
                |> List.sort

            lines.Add $"{name}: median {times[n / 2]:F1} ms of {n} (min {List.head times:F1}, max {List.last times:F1})"

        let ok name (r: Result<'T, EvaluationDiagnostic list>) =
            match r with
            | Ok _ -> ()
            | Error d -> failwithf "%s: %A" name d

        // As the debugger warms the compiler up when the debuggee loads FSharp.Core
        // (FSHARP_EE_PERF_COMPILER=0 skips it). Here it runs beside reading and `Create`.
        let compilerWarmUp =
            if Environment.GetEnvironmentVariable "FSHARP_EE_PERF_COMPILER" <> "0" then
                Some(
                    Threading.Tasks.Task.Run(fun () ->
                        let sw = Diagnostics.Stopwatch.StartNew()
                        EvaluationSession.WarmUpCompiler() |> ignore
                        sw.Elapsed.TotalMilliseconds)
                )
            else
                None

        let modules = time "read modules" (fun () -> debuggeeModules false)
        lines.Add $"modules: {modules.Length}"
        let s = time "EvaluationSession.Create (cold)" (fun () -> EvaluationSession.Create modules)

        compilerWarmUp
        |> Option.iter (fun t ->
            time "wait for WarmUpCompiler" (fun () -> t.Wait())
            lines.Add $"WarmUpCompiler (beside the above): {t.Result:F1} ms")
        let add = frameOf (methodOf fixtures "add")
        let withLocals = frameOf (methodOf fixtures "withLocals")

        // As the debugger's session is warmed up on the first break (FSHARP_EE_PERF_WARMUP=0 skips it).
        if Environment.GetEnvironmentVariable "FSHARP_EE_PERF_WARMUP" <> "0" then
            time "WarmUp" (fun () -> s.WarmUp(withLocals.AssemblyName, withLocals.MethodToken)) |> ignore

        time "CompileExpression 'x + y' (first)" (fun () -> s.CompileExpression(add, "x + y")) |> ok "x + y"

        time "CompileExpression 'List.sum moduleValue' (second)" (fun () -> s.CompileExpression(add, "List.sum moduleValue"))
        |> ok "List.sum"

        time "CompileLocals withLocals (first)" (fun () -> s.CompileLocals(withLocals, false)) |> ok "locals"
        median "CompileExpression 'x + y' (warm)" 20 (fun () -> s.CompileExpression(add, "x + y"))
        median "CompileExpression 'List.map (fun i -> i * 2) moduleValue' (warm)" 20 (fun () -> s.CompileExpression(add, "List.map (fun i -> i * 2) moduleValue"))
        median "CompileLocals withLocals (warm)" 20 (fun () -> s.CompileLocals(withLocals, false))
        (s :> IDisposable).Dispose()
        let s2 = time "EvaluationSession.Create (again)" (fun () -> EvaluationSession.Create modules)
        time "CompileExpression 'x + y' (first, new session)" (fun () -> s2.CompileExpression(add, "x + y")) |> ok "x + y"
        (s2 :> IDisposable).Dispose()
        File.AppendAllLines(path, lines)

[<Fact>]
let ``locals of FSharp.Core types show F# names`` () =
    let frameMethod = methodOf fixtures "withFSharpTypes"
    let f = fun (xs: int list) -> Some(string xs.Length)

    let args =
        [ box [ 1 ]; box f; box (Some [ "a" ]); box (ValueSome [ 1 ]); box (ref 0); box [ () ]; box [ (1, Some 'c') ] ]

    Assert.Equal<(string * string) list>(
        [
            "xs", "int list"
            "f", "int list -> string option"
            "names", "string list option"
            "counts", "int list voption"
            // `let total = ref 0` reads as a captured mutable local would (docs/DESIGN.md §6.4)
            "total", "int"
            "units", "unit list"
            "pairs", "(int * char option) list"
        ],
        localsOf (frameOf frameMethod) frameMethod args false
        |> List.map (fun (name, _, ty, _) -> name, ty)
    )

[<Fact>]
let ``locals of anonymous record types`` () =
    let frameMethod = methodOf fixtures "withAnonymousRecords"
    let r = {| Name = "anon"; Count = 3 |}

    let args =
        [ box 3; box r; box [ r ]; box (Some r); box (struct {| A = 3; B = "b" |}); box {| Inner = r; Tag = 'n' |} ]

    Assert.Equal<(string * string) list>(
        [
            "x", "int"
            "r", "{| Count: int; Name: string |}"
            "inList", "{| Count: int; Name: string |} list"
            "inOption", "{| Count: int; Name: string |} option"
            "s", "struct {| A: int; B: string |}"
            "nested", "{| Inner: {| Count: int; Name: string |}; Tag: char |}"
        ],
        localsOf (frameOf frameMethod) frameMethod args false
        |> List.map (fun (name, _, ty, _) -> name, ty)
    )

