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

/// Break 1: a module function with locals of many F# shapes, and a parameter whose F# type IL has lost.
let inspect (xs: int list) (name: string) (trip: float<m>) =
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
    let unitValue = ()
    let boxed: obj = box [ 1; 2 ]
    let add = fun (a: int) (b: int) -> a + b
    // C# renders it, through its DebuggerDisplay.
    let dict = Collections.Generic.Dictionary<string, int>()
    dict.Add("a", 1)
    dict.Add("b", 2)
    Debugger.Break()

    printfn
        "%A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %s %d %d"
        point shape rect five structUnion opt none someShape result tuple structTuple empty nested array map set anon cell error unitValue boxed (add 1 2) xs name longList.Length dict.Count

/// Break 2: inside a closure that captures an immutable and a mutable local.
let closure () =
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

/// Break 5: inside a task state machine.
let work (delay: int) =
    task {
        do! System.Threading.Tasks.Task.Delay delay
        Debugger.Break()
        return delay
    }

let twice x = x * 2

/// Break 6: steps over an F# function and a BCL call (see their return values).
let returns () =
    let xs = [ 1; 2; 3 ]
    Debugger.Break()
    let n = twice (List.length xs)
    let s = String.Concat("a", string n)
    s

/// Break 7: no Debugger.Break here; Probe-Debugger.ps1 sets a conditional breakpoint or a tracepoint
/// on the marked line.
let loop () =
    let mutable total = 0

    for i in 1..10 do
        total <- total + i // probe: breakpoint

    total

[<EntryPoint>]
let main argv =
    if Array.contains "--wait" argv then
        Console.WriteLine $"PID {Process.GetCurrentProcess().Id}"

        while not Debugger.IsAttached do
            Thread.Sleep 100

    // Top-level code of the other files runs on the first use of their values: breaks S1 and S2 with --startup.
    Startup.startupCheck |> ignore
    TestDebuggee.Settings.Checks.ready |> ignore

    inspect [ 1; 2; 3 ] "test" 2.5<m>
    closure () |> ignore
    Counter(10).Increment 2 |> ignore
    generic [ 1; 2 ] (3, "c") |> ignore
    (work 1).Wait()
    returns () |> ignore
    loop () |> ignore
    0
