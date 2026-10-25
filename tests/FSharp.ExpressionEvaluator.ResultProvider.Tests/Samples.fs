/// F# types covering each union representation and record kind the result provider must read.
module FSharp.ExpressionEvaluator.Tests.Samples

type Shape =
    | Circle of radius: float
    | Rect of float * float
    | Empty

/// All-nullary unions use a `_tag` field.
type Two =
    | A
    | B

/// Four or more cases: `_tag` field plus case subclasses.
type Five =
    | P
    | Q of int
    | R
    | S of string * int
    | T

type Single = Single of id: int * name: string

[<Struct>]
type StructUnion =
    | SA of a: int
    | SB of b: string

type Gen<'T> =
    | GNone
    | GSome of 'T

type Point = { X: int; Y: int }

[<Struct>]
type StructPoint = { SX: int; SY: int }

/// Fields whose values are null at runtime: `()` and `None`, directly and through a type parameter.
type Holder<'T> = { U: unit; O: int option; G: 'T }

exception MyError of code: int * string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Point =
    let origin = { X = 0; Y = 0 }

/// A closure capturing an immutable value and a mutable one (which becomes a ref cell).
let makeClosure () =
    let captured = 5
    let mutable counter = 0

    fun (x: int) ->
        counter <- counter + x
        captured + x

let curried = fun (x: int) (y: int) -> x + y
