module TestDebuggee.Program

open System
open System.Diagnostics
open System.Threading

type Point = { X: int; Y: int }

type Shape =
    | Circle of radius: float
    | Rect of float * float

let moduleList = [ 10; 20; 30 ]

[<Measure>]
type m

/// The CLR type has neither: the Type column shows them from the expression compiler's F# type.
type Ids = int list

let distance = 3.0<m>

let ids: Ids = [ 1; 2 ]

type Five =
    | P
    | Q of int
    | R
    | S of string * int
    | T

[<Struct>]
type StructUnion =
    | SA of a: int
    | SB of b: string

exception MyError of code: int * string

/// Names that F# writes in double backticks: with spaces, and keywords.
type Odd = { ``my field``: int; ``type``: string }

type OddUnion =
    | ``My Case`` of ``the value``: int
    | ``Other Case``

type ``Odd Box`` = { Inside: int }

/// A generic union: its case fields' full names are a match on the case without type arguments.
type Tree<'T> =
    | Leaf
    | Node of 'T * Tree<'T>

/// An abbreviation: the Type column has its name from the expression compiler. A module value,
/// since locals other than parameters have no F# type recorded.
type ``My Ids`` = int list

let myIds: ``My Ids`` = [ 4 ]

/// Break 1: a module function with locals of many F# shapes, a parameter whose F# type IL has lost,
/// and a local that shadows a parameter (Locals shows both).
let inspect (xs: int list) (name: string) (trip: float<m>) =
    let name = name.ToUpperInvariant()
    let point = { X = 1; Y = 2 }
    let shape = Circle 2.0
    let rect = Rect(1.0, 2.5)
    let five = S("x", 2)
    let structUnion = SB "b"
    let opt = Some 42
    let none: int option = None
    let someShape = Some(Circle 1.5)
    let result: Result<int, string> = Error "failed"
    let tuple = (1, "a")
    let structTuple = struct (1.5, 'c')
    let empty: int list = []
    let nested = [ [ 1; 2 ]; [ 3 ] ]
    let longList = [ 1..200 ]
    let array = [| 1.0; 2.5 |]
    let map = Map.ofList [ "one", 1; "two", 2 ]
    let set = Set.ofList [ 3; 1; 2 ]
    let anon = {| Name = "anon"; Count = 3 |}
    let cell = ref 7
    let error = MyError(404, "not found")
    // Typed as the exception itself, not `exn`.
    let typedError = MyError(500, "typed")
    let unitValue = ()
    let boxed: obj = box [ 1; 2 ]
    let add = fun (a: int) (b: int) -> a + b
    // A Lazy shows its value once created, and isn't forced by the display.
    let created = lazy [ 1; 2 ]
    created.Force() |> ignore
    let pending = lazy (printfn "forced"; 3)
    let big = 12345678901234567890123I
    let ``the sum`` = 3 + 4
    let odd = { ``my field`` = 1; ``type`` = "t" }
    let oddUnion = ``My Case`` 7
    let oddBox = { Inside = 2 }
    // C# renders it, through its DebuggerDisplay.
    let dict = Collections.Generic.Dictionary<string, int>()
    dict.Add("a", 1)
    dict.Add("b", 2)
    // Primitives, which the C# provider renders: their rows show F# literals.
    let ratio = 2.0
    let text = "a\tb"
    let money = 1.5M
    let letter = 'q'
    let count64 = 5L
    // More rows for Add Watch: a two-dimensional array (C# renders it), a generic union, a map with a
    // key that is no literal, and a tuple with items in `Rest`.
    let grid = Array2D.init 2 3 (fun i j -> i * 3 + j)
    let tree = Node(1, Node(2, Leaf))
    let points = Map.ofList [ { X = 1; Y = 2 }, "p" ]
    let big9 = (1, 2, 3, 4, 5, 6, 7, 8, 9)
    Debugger.Break()

    printfn
        "%A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %s %d %d %A %b %A"
        point shape rect five structUnion opt none someShape result tuple structTuple empty nested array map set anon cell error unitValue boxed (add 1 2) xs name longList.Length dict.Count
        created.Value pending.IsValueCreated big
    printfn "%d %A %A %A %A" ``the sum`` odd oddUnion oddBox myIds
    printfn "%f %s %M %c %d %A %A %A %A %A" ratio text money letter count64 grid tree points big9 typedError

/// Break 2: inside a closure that captures an immutable and a mutable local. On the call stack,
/// `closure` shows its parameter's F# type.
let closure (start: float<m>) =
    let captured = 5
    let mutable counter = 0

    let f x =
        counter <- counter + x
        Debugger.Break()
        captured + x

    f 1 |> ignore
    counter

type Counter(start: int) =
    let mutable count = start

    /// Break 3: an instance member with a class let field.
    member this.Increment(by: int) =
        count <- count + by
        Debugger.Break()
        count

/// Break 4: a generic function with a tuple parameter.
let generic (value: 'T) (pair: int * string) =
    Debugger.Break()
    value, pair

/// Break 5: inside a task state machine, after awaits. Its variables are hoisted into fields:
/// `doubled`, a `label` that shadows the parameter, and `squared`, out of scope after the loop.
/// The builder's own (the awaiters, the loop's enumerator) are under `[compiler generated]`.
let work (delay: int) (label: string) =
    task {
        let doubled = delay * 2
        do! System.Threading.Tasks.Task.Delay delay
        let label = label + "!"

        for item in [ 1; 2 ] do
            let squared = item * item
            do! System.Threading.Tasks.Task.Yield()
            ignore squared

        do! System.Threading.Tasks.Task.Yield()
        Debugger.Break()
        return doubled + label.Length
    }

let twice x = x * 2

/// Break 6: steps over an F# function and a BCL call (see their return values). The list is the
/// argument of `function`, `_arg1`, which Locals lists under its `[compiler generated]` row.
let returns (label: string) =
    function
    | Some(xs: int list) ->
        Debugger.Break()
        let n = twice (List.length xs)
        let s = String.Concat(label, string n)
        s
    | None -> label

/// Break 7: no Debugger.Break here; Probe-Debugger.ps1 sets a conditional breakpoint or a tracepoint
/// on the marked line.
let loop () =
    let mutable total = 0

    for i in 1..10 do
        total <- total + i // probe: breakpoint

    total

/// Call stack names of compiler-generated code, with --frames instead of breaks 1-7. Break F1: in a
/// lambda. F2: in an inner function. F3: in a lambda inside it.
let lambdaFrames (xs: int list) =
    let firsts =
        xs
        |> List.filter (fun x ->
            if x = 1 then
                Debugger.Break()

            x > 0)

    let scale (k: int) =
        Debugger.Break()

        firsts
        |> List.map (fun x ->
            if x = 1 then
                Debugger.Break()

            x * k)

    scale 2

/// Break F4: in the code of an `async { }`, after a `do!`.
let asyncFrame (n: int) =
    async {
        do! Async.Sleep 1
        Debugger.Break()
        return n + 1
    }

/// Break F5: in a `seq { }`.
let seqFrame () =
    seq {
        yield 1
        Debugger.Break()
        yield 2
    }

/// Break F6: in a lambda that returns an async, with a parameter of its own: not the async's code.
let asyncLambda (urls: string list) =
    urls
    |> List.map (fun url ->
        Debugger.Break()
        async { return url.Length })

let private frames () =
    lambdaFrames [ 1; 2 ] |> ignore
    asyncFrame 1 |> Async.RunSynchronously |> ignore
    seqFrame () |> Seq.toList |> ignore
    asyncLambda [ "abc" ] |> Async.Parallel |> Async.RunSynchronously |> ignore

[<EntryPoint>]
let main argv =
    if Array.contains "--wait" argv then
        Console.WriteLine $"PID {Process.GetCurrentProcess().Id}"

        while not Debugger.IsAttached do
            Thread.Sleep 100

    if Array.contains "--frames" argv then
        frames ()
        exit 0

    // Top-level code of the other files runs on the first use of their values: breaks S1 and S2 with --startup.
    Startup.startupCheck |> ignore
    TestDebuggee.Settings.Checks.ready |> ignore

    inspect [ 1; 2; 3 ] "test" 2.5<m>
    closure 1.0<m> |> ignore
    Counter(10).Increment 2 |> ignore
    generic [ 1; 2 ] (3, "c") |> ignore
    (work 1 "w").Wait()
    returns "a" (Some [ 1; 2; 3 ]) |> ignore
    loop () |> ignore
    0
