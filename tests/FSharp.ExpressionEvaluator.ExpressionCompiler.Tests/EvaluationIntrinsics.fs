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

    /// Variables declared through CreateVariable, each a ref cell of its type, by name.
    static member val Cells = Dictionary<string, obj>()

    /// The type, payload id and payload each CreateVariable was called with, by name.
    static member val Created = Dictionary<string, System.Type * System.Guid * byte[]>()

    static member GetObjectByAlias(name: string) : obj =
        match IntrinsicMethods.Cells.TryGetValue name with
        | true, cell -> cell.GetType().GetProperty("Value").GetValue cell
        | _ -> IntrinsicMethods.Variables[name]

    static member CreateVariable(t: System.Type, name: string, payloadId: System.Guid, payload: byte[]) : unit =
        let initial = if t.IsValueType then System.Activator.CreateInstance t else null
        IntrinsicMethods.Cells[name] <- System.Activator.CreateInstance(typedefof<_ ref>.MakeGenericType t, [| initial |])
        IntrinsicMethods.Created[name] <- (t, payloadId, payload)

    static member GetVariableAddress<'T>(name: string) : byref<'T> =
        let cell = IntrinsicMethods.Cells[name] :?> 'T ref
        &cell.contents
