module TestDebuggee.Program

open System
open System.Diagnostics
open System.Threading

type Point = { X: int; Y: int }

type Shape =
    | Circle of radius: float
    | Rect of float * float

let moduleList = [ 10; 20; 30 ]

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

/// Break 1: a module function with locals of many F# shapes.
let inspect (xs: int list) (name: string) =
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
    Debugger.Break()

    printfn
        "%A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %A %s %d"
        point shape rect five structUnion opt none someShape result tuple structTuple empty nested array map set anon cell error unitValue boxed (add 1 2) xs name longList.Length

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

[<EntryPoint>]
let main argv =
    if Array.contains "--wait" argv then
        Console.WriteLine $"PID {Process.GetCurrentProcess().Id}"

        while not Debugger.IsAttached do
            Thread.Sleep 100

    inspect [ 1; 2; 3 ] "test"
    closure () |> ignore
    Counter(10).Increment 2 |> ignore
    generic [ 1; 2 ] (3, "c") |> ignore
    (work 1).Wait()
    0
