/// Ordinary F# code for the sweep of frame shapes (EvaluationTests, "frame shapes"): each function or
/// member is the simplest one that gives a frame of its shape. Generated frames (closures, state
/// machines) call `probe` so that the tests can find their method and IL offset.
module FSharp.ExpressionEvaluator.Tests.SweepFixtures

#nowarn "9" // NativePtr
#nowarn "51" // address-of

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Microsoft.FSharp.Quotations

/// Where the generated fixtures were when they called `probe`, by label: the method and the IL
/// offset in it.
let probes = Dictionary<string, Reflection.MethodBase * int>()

[<MethodImpl(MethodImplOptions.NoInlining)>]
let probe (label: string) =
    let caller = Diagnostics.StackFrame(1, false)
    lock probes (fun () -> probes[label] <- (caller.GetMethod(), caller.GetILOffset()))

// Byref-like and pointer values.

let spanParameter (span: Span<int>) (n: int) = span[0] + n

let readOnlySpanParameter (text: ReadOnlySpan<char>) (n: int) = text.Length + n

let readOnlySpanLocal (text: string) =
    let chars = text.AsSpan()
    let length = chars.Length
    length

[<Struct; IsByRefLike>]
type Window =
    val Start: int
    val Length: int
    new(start, length) = { Start = start; Length = length }
    member this.End(extra: int) = this.Start + this.Length + extra

let byrefLikeParameter (window: Window) (n: int) = window.Start + n

let nativePointer (p: nativeptr<int>) (n: int) = NativePtr.read p + n

let voidPointer (p: voidptr) (n: int) = n

let nativeInt (p: nativeint) (n: int) = int p + n

let outrefGeneric (result: outref<'T>) (value: 'T) = result <- value

// Struct frames.

[<Struct>]
type SPoint =
    { X: int; Y: int }

    member this.Sum(k: int) = this.X + this.Y + k

[<Struct>]
type SShape =
    | SCircle of radius: float
    | SSquare of side: float

    member this.Area(scale: float) =
        match this with
        | SCircle r -> r * r * scale
        | SSquare s -> s * s * scale

[<Struct>]
type SCounter =
    val mutable Count: int
    new(count) = { Count = count }

    member this.Bump(by: int) =
        this.Count <- this.Count + by
        this.Count

let structTuple (struct (a: int, b: string)) (n: int) = a + b.Length + n

// Members.

type Repo<'T when 'T: comparison>(items: 'T list) =
    member this.Find<'K when 'K: equality>(key: 'K, pick: 'T -> 'K) = items |> List.tryFind (fun i -> pick i = key)
    member this.Largest(extra: 'T) = List.max (extra :: items)
    static member Create(xs: 'T list) = Repo xs
    member this.Count = items.Length

type Settings() =
    let mutable level = 1

    member this.Level
        with get () = level
        and set (v: int) = level <- v

    member this.Item
        with get (i: int) = level + i
        and set (i: int) (v: int) = level <- v + i

    static member Scale(n: int) = n * 2

type IShape =
    abstract Area: scale: float -> float

type Square(side: float) =
    interface IShape with
        member this.Area(scale) = side * side * scale

let makeShape (side: float) =
    { new IShape with
        member _.Area(scale) = side * scale }

module Extensions =
    type String with
        member this.Twice(n: int) = this + this + string n

[<Extension>]
type ListExtensions =
    [<Extension>]
    static member Total(xs: int list, n: int) = List.sum xs + n

type Animal(name: string) =
    let length = name.Length

    do
        let shout = name.ToUpperInvariant()
        ignore shout

    new() = Animal "anon"

    abstract Speak: times: int -> string
    default this.Speak(times) = String.replicate times name

    member this.Length = length

type Dog(name: string) =
    inherit Animal(name)
    override this.Speak(times) = "woof " + name + string times

type Vector =
    { DX: float; DY: float }

    static member (+)(a: Vector, b: Vector) = { DX = a.DX + b.DX; DY = a.DY + b.DY }

type Options =
    static member Get(?count: int, ?label: string) = defaultArg count 1, defaultArg label "none"
    static member Sum([<ParamArray>] values: int[]) = Array.sum values

/// Units of measure are erased: IL has no type parameter for `'u`.
type Quantity<[<Measure>] 'u, 'T>(value: float<'u>, tag: 'T) =
    member this.Scaled(k: float) = value * k
    member this.Tagged(extra: 'T) = (tag, extra)

let measuredAndGeneric (x: float<'u>) (tag: 'T) = (x, tag)

/// A self identifier other than `this`, and curried parameters of a member.
type Named(start: int) =
    member x.Plus(n: int) = start + n
    member _.Curried (a: int) (b: int) = start + a + b

/// `as self` in a constructor: `self` is a ref cell until the object is built.
type Initialized() as self =
    let mutable ready = false
    do self.Ready()
    member this.Ready() = ready <- true

/// A class with a `static let`: its static initializer is a frame.
type WithStatics() =
    static let table =
        let size = 3
        Array.zeroCreate<int> size

    static member Table = table
    static member Lookup(i: int) = table[i] + i

[<Struct>]
type SPair<'T> =
    { A: 'T; B: 'T }

    member this.Swap(n: int) = struct (this.B, this.A, n)

type Token =
    | Word of string
    | Number of int

    member this.Describe(prefix: string) =
        match this with
        | Word w -> prefix + w
        | Number n -> prefix + string n

type Range =
    { Low: int; High: int }

    member this.Contains(n: int) = this.Low <= n && n <= this.High

// Functions.

/// Inner functions that capture nothing are lifted to methods of the module.
let withLifted (n: int) =
    let helper (x: int) = x + 1
    let generic (x: 'a) = [ x; x ]
    helper n + (generic n).Length

let fixedPointer (values: int[]) =
    use p = fixed values
    let first = NativePtr.read p
    first

let withRecInner (limit: int) =
    let rec go (i: int) (acc: int) = if i > limit then acc else go (i + 1) (acc + i)
    go 0 0

let rec isEven (n: int) = if n = 0 then true else isOdd (n - 1)

and isOdd (n: int) = if n = 0 then false else isEven (n - 1)

let inline twice (x: ^T) = x + x

let callsInline (n: int) =
    let doubled = twice n
    doubled

let (|Even|Odd|) (n: int) = if n % 2 = 0 then Even else Odd

let (|Positive|_|) (n: int) = if n > 0 then Some n else None

let measured (x: float<'u>) (factor: float) = x * factor

let applyFunctions (f: int * int -> int) (g: int -> int -> int) (n: int) = f (n, n) + g n n

type Shape =
    | Circle of radius: float
    | Rect of width: float * height: float

type Point = { PX: int; PY: int }

let matchArms (pair: int * string) (shape: Shape) (xs: int list) (p: Point) =
    let fromTuple =
        match pair with
        | (a, b) -> a + b.Length

    let fromUnion =
        match shape with
        | Circle radius -> radius
        | Rect(width, height) -> width * height

    let fromList =
        match xs with
        | head :: tail -> head + tail.Length
        | [] -> 0

    let fromRecord =
        match p with
        | { PX = px; PY = py } as whole -> px + py + whole.PX

    float (fromTuple + fromList + fromRecord) + fromUnion

let loops (n: int) (xs: int list) =
    let mutable total = 0

    for i in 1..n do
        total <- total + i

    for x in xs do
        total <- total + x

    let mutable k = 0

    while k < n do
        k <- k + 1

    total + k

let withUse (n: int) =
    use stream = new IO.MemoryStream(Array.zeroCreate<byte> n)
    let length = int stream.Length
    length

let withHandler (n: int) =
    try
        if n > 0 then failwith "boom" else n
    with e ->
        let message = e.Message
        message.Length

// Types of variables.

type Color =
    | Red = 1
    | Green = 2

type Expression =
    | Literal of int
    | Add of Expression * Expression

type Transform = int -> int

let variableTypes
    (color: Color)
    (maybe: Nullable<int>)
    (text: string | null)
    (anon: {| Name: string; Tags: string list |} list)
    (vo: int voption)
    (result: Result<int, string>)
    (jagged: int[][])
    (grid: int[,])
    (lazyValue: Lazy<int>)
    (choice: Choice<int, string, bool>)
    (quote: Expr<int>)
    (big: int * int * int * int * int * int * int * int)
    (expr: Expression)
    (transform: Transform)
    (handler: EventHandler)
    =
    let total = int color + maybe.GetValueOrDefault() + jagged.Length + grid.Length + transform 1
    total

type Ticker() =
    let ticked = Event<int>()
    member this.Ticked = ticked.Publish
    member this.Tick(n: int) = ticked.Trigger n

// Generated frames.

let nested3 (a: int) =
    let f1 (b: int) =
        let f2 (c: int) =
            let f3 (d: int) =
                probe "nested3"
                a + b + c + d

            f3 1

        f2 2

    f1 3

type Holder<'T>(value: 'T) =
    member this.Pair<'U>(other: 'U) =
        [ 1; 2 ]
        |> List.map (fun i ->
            probe "genericClosure"
            value, other, i)

let asyncShapes (n: int) (xs: int list) =
    async {
        let! first = async { return n + 1 }
        use! resource = async { return new IO.MemoryStream() }

        try
            for x in xs do
                let squared = x * x
                probe "asyncFor"
                ignore squared
        finally
            probe "asyncFinally"

        return first + int resource.Length
    }

let taskShapes (n: int) (xs: int list) =
    task {
        let! first = Threading.Tasks.Task.FromResult(n + 1)
        use! resource = Threading.Tasks.Task.FromResult(new IO.MemoryStream())

        try
            for x in xs do
                let squared = x * x
                do! Threading.Tasks.Task.Yield()
                probe "taskFor"
                ignore squared
        finally
            probe "taskFinally"

        return first + int resource.Length
    }

let seqShapes (n: int) =
    seq {
        use stream = new IO.MemoryStream()
        let size = n + int stream.Length
        probe "seqUse"
        yield size
        yield! [ n; n ]
    }

let background (n: int) =
    backgroundTask {
        let doubled = n * 2
        do! Threading.Tasks.Task.Yield()
        probe "backgroundTask"
        return doubled
    }

let lazyBody (n: int) =
    lazy
        (let doubled = n * 2
         probe "lazy"
         doubled)

type MaybeBuilder() =
    member _.Bind(x: 'a option, f: 'a -> 'b option) = Option.bind f x
    member _.Return(x: 'a) = Some x

let maybe = MaybeBuilder()

let customBuilder (n: int) =
    maybe {
        let! a = Some n
        let! b = Some(a + 1)
        probe "builder"
        return a + b
    }

/// `static let` values in closures and state machines of a class in a module: their classes are
/// nested in the module's class, not in the class.
type Registry(name: string) =
    static let multiplier = 3
    static let mutable calls = 0

    static member Lookup(i: int) =
        [ i ]
        |> List.map (fun x ->
            probe "registryStaticLambda"
            x * multiplier)

    /// The lambda captures `this`, for `name`.
    member this.ScaleAll(xs: int list) =
        xs
        |> List.map (fun x ->
            calls <- calls + 1
            probe "registryLambda"
            x * multiplier + name.Length)

    /// The lambda captures nothing of `this`.
    member this.Plain(xs: int list) =
        xs
        |> List.map (fun x ->
            probe "registryPlainLambda"
            x * multiplier)

    member this.InTask(n: int) =
        task {
            do! Threading.Tasks.Task.Yield()
            probe "registryTask"
            return n * multiplier + name.Length
        }

    member this.InAsync(n: int) =
        async {
            do! Async.Sleep 0
            probe "registryAsync"
            return n * multiplier + name.Length
        }

    member this.InSeq(n: int) =
        seq {
            probe "registrySeq"
            yield n * multiplier + name.Length
        }

/// Runs the generated fixtures once, so that `probes` has their frames.
let runGenerated =
    lazy
        (nested3 1 |> ignore
         (Registry "reg").ScaleAll [ 1 ] |> ignore
         (Registry "reg").Plain [ 1 ] |> ignore
         Registry.Lookup 1 |> ignore
         ((Registry "reg").InTask 1).Wait()
         (Registry "reg").InAsync 1 |> Async.RunSynchronously |> ignore
         (Registry "reg").InSeq 1 |> Seq.toList |> ignore
         (Holder "v").Pair 2 |> ignore
         asyncShapes 1 [ 2 ] |> Async.RunSynchronously |> ignore
         (taskShapes 1 [ 2 ]).Wait()
         seqShapes 1 |> Seq.toList |> ignore
         (background 1).Wait()
         (lazyBody 1).Force() |> ignore
         customBuilder 1 |> ignore)
