/// F# code whose compiled methods the frame-name tests read back from metadata.
module FSharp.ExpressionEvaluator.Tests.Fixtures

let add (x: int) (y: int) = x + y

let addTupled (x: int, y: int) = x + y

let mixed (a: int, b: string) (c: float) = string a + b + string c

let noArgs () = 42

let identity (x: 'T) = x

let describe (xs: int list) (f: int -> string) (pair: int * string) (o: int option) = xs, f, pair, o

let (+.) (a: int) (b: int) = a + b

type Person(name: string) =
    member _.Name = name
    member _.Greet(greeting: string, times: int) = String.replicate times greeting + name
    static member (+)(a: Person, b: Person) = Person(a.Name + b.Name)

type Box<'T>(value: 'T) =
    member _.Map(f: 'T -> 'U) = Box(f value)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Person =
    let create name = Person name

let makeClosure () =
    let captured = 5
    fun (x: int) -> captured + x

let makeUnitClosure () =
    let captured = 5
    fun () -> captured

let makeCurriedClosure () =
    let captured = 5
    fun (x: int) (y: int) -> captured + x + y

let numbers () =
    seq {
        yield 1
        yield 2
    }

let work () = task { return 42 }
