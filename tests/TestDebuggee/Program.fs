module TestDebuggee.Program

open System
open System.Diagnostics
open System.Threading

type Point = { X: int; Y: int }

type Shape =
    | Circle of radius: float
    | Rect of float * float

let moduleList = [ 10; 20; 30 ]

/// Break 1: a module function with locals of many F# shapes.
let inspect (xs: int list) (name: string) =
    let point = { X = 1; Y = 2 }
    let shape = Circle 2.0
    let opt = Some 42
    let none: int option = None
    let tuple = (1, "a")
    let empty: int list = []
    let nested = [ [ 1; 2 ]; [ 3 ] ]
    let longList = [ 1..200 ]
    Debugger.Break()
    printfn "%A %A %A %A %A %A %A %A %s %d" point shape opt none tuple empty nested xs name longList.Length

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

[<EntryPoint>]
let main argv =
    if Array.contains "--wait" argv then
        Console.WriteLine $"PID {Process.GetCurrentProcess().Id}"

        while not Debugger.IsAttached do
            Thread.Sleep 100

    inspect [ 1; 2; 3 ] "test"
    closure () |> ignore
    Counter(10).Increment 2 |> ignore
    0
