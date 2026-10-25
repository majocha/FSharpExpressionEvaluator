namespace Microsoft.VisualStudio.Debugger.Clr

open System.Collections.Generic

/// A stand-in for the debugger's intrinsic methods, which compiled expressions call to read the
/// debugger's aliases. The tests set the values the debugger would hold.
[<AbstractClass; Sealed>]
type IntrinsicMethods =
    static member val Exception: exn = null with get, set

    /// Return values, the most recent first.
    static member val ReturnValues: obj list = [] with get, set

    static member val Variables = Dictionary<string, obj>()

    static member GetException() : exn = IntrinsicMethods.Exception

    static member GetStowedException() : exn = null

    static member GetReturnValue(index: int) : obj = IntrinsicMethods.ReturnValues[index]

    static member GetObjectByAlias(name: string) : obj = IntrinsicMethods.Variables[name]
