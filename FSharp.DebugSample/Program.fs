/// Sample console application for live-testing the F# Expression Evaluator
/// extension.
///
/// To exercise the formatter:
///   1. Install the VSIX extension in Visual Studio.
///   2. Open this project in VS and set a breakpoint on the line that reads
///      `printfn ">>> Locals are ready for inspection."` in the `main` function.
///   3. Press F5 to start debugging.
///   4. When the breakpoint is hit, open the Locals / Watch window and compare
///      the "Type" column against the table in README.md.
///
/// What to look for
/// ----------------
/// Without the extension:
///   color     Red                    FSharp.DebugSample.Color
///   shape     Circle 2.718...        FSharp.DebugSample.Shape
///   pt        {X = 0.0; Y = 1.0}    FSharp.DebugSample.Point
///   pair      (42, "hello")          System.Tuple`2[System.Int32,System.String]
///   anon      {Age = 30; Name = ...} <>f__AnonymousType0`2[System.Int32,System.String]
///
/// With the F# EE extension:
///   color     Red                    Color
///   shape     Circle 2.718...        Shape
///   pt        {X = 0.0; Y = 1.0}    Point
///   pair      (42, "hello")          (int * string)
///   anon      {Age = 30; Name = ...} {| Age: int; Name: string |}

module FSharp.DebugSample

// ---------------------------------------------------------------------------
// Type definitions used as locals
// ---------------------------------------------------------------------------

/// Simple discriminated union (no data).
type Color = Red | Green | Blue

/// DU with payload – Circle carries a single float, Rectangle carries two.
type Shape =
    | Circle    of radius: float
    | Rectangle of width: float * height: float

/// F# record.
type Point = { X: float; Y: float }

/// Generic DU – a binary tree.
type BinaryTree<'T> =
    | Empty
    | Node of value: 'T * left: BinaryTree<'T> * right: BinaryTree<'T>

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main _ =

    // ---------- primitives & unit ----------
    let n : int    = 42
    let f : float  = 3.14159
    let b : bool   = true
    let c : char   = 'A'        // C# EE shows "65 'A'"; F# EE shows "'A'"
    let s : string = "hello"
    let u : unit   = ()         // C# EE shows "{}";     F# EE shows "()"

    // ---------- tuples ----------
    let pair   = (42, "hello")                  // (int * string)
    let triple = (1, 2.0, "three")              // (int * float * string)
    let nested = ((1, 2), (true, 'x'))          // ((int * int) * (bool * char))

    // ---------- F# standard library types ----------
    let opt      = Some 42                      // int option
    let noVal    : int option = None
    let vopt     = ValueSome 3.14               // float voption
    let lst      = [ 1; 2; 3 ]                  // int list
    let arr      = [| "a"; "b"; "c" |]          // string[]
    let arr2d    = Array2D.init 2 2 (fun i j -> i + j) // int[,]
    let mapped   = Map.ofList [ ("a", 1); ("b", 2) ]   // Map<string, int>
    let s_et     = Set.ofList [ 1; 2; 3 ]       // Set<int>
    let r        = ref 99                        // int ref

    // ---------- DU instances ----------
    let color = Green                            // Color
    let shape = Circle 2.71828                   // Shape

    // ---------- record ----------
    let pt = { X = 0.0; Y = 1.0 }              // Point

    // ---------- anonymous records ----------
    // Ref anonymous record: the type column should show {| Age: int; Name: string |}
    let anon        = {| Name = "Alice"; Age = 30 |}
    // Struct anonymous record: should show struct {| X: float; Y: float |}
    let anonStruct  = struct {| X = 1.0; Y = 2.0 |}

    // ---------- generic DU ----------
    let tree = Node(1, Empty, Node(2, Empty, Empty))  // BinaryTree<int>

    // ---------- nested / combined ----------
    let optPoint    = Some { X = 3.0; Y = 4.0 }      // Point option
    let listOfTuples = [ (1, 'a'); (2, 'b') ]          // (int * char) list
    let listOfAnon   = [ {| Id = 1; Tag = "x" |}
                         {| Id = 2; Tag = "y" |} ]     // {| Id: int; Tag: string |} list

    // ---------- function value ----------
    let add : int -> int -> int = fun x y -> x + y     // int -> int -> int

    // Set a breakpoint on the next line and inspect the Locals window.
    printfn ">>> Locals are ready for inspection."

    // Silence "unused variable" warnings by printing a summary line.
    printfn "n=%d f=%.2f b=%b c=%c s=%s u=%A" n f b c s u
    printfn "pair=%A triple=%A nested=%A" pair triple nested
    printfn "opt=%A noVal=%A vopt=%A" opt noVal vopt
    printfn "lst=%A arr=%A" lst arr
    printfn "mapped=%A s_et=%A r=%A" mapped s_et r
    printfn "color=%A shape=%A pt=%A" color shape pt
    printfn "anon=%A anonStruct=%A" anon anonStruct
    printfn "tree=%A" tree
    printfn "optPoint=%A listOfTuples=%A" optPoint listOfTuples
    printfn "arr2d=%A" arr2d
    printfn "listOfAnon=%A" listOfAnon
    printfn "add 1 2 = %d" (add 1 2)
    0
