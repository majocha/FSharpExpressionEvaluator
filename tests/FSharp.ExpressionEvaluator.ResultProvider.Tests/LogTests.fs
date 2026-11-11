module FSharp.ExpressionEvaluator.Tests.LogTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FSharp.ExpressionEvaluator

let private tempLog () =
    let dir = Path.Combine(Path.GetTempPath(), "FSharpExpressionEvaluator.Tests", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, "ee.log")

[<Fact>]
let ``a log larger than the limit moves to log.1`` () =
    let file = tempLog ()
    File.WriteAllText(file, String('a', 100))
    File.WriteAllText(file + ".1", "older")

    Assert.False(Log.rollOver file 100L)
    Assert.True(File.Exists file)

    File.AppendAllText(file, "b")
    Assert.True(Log.rollOver file 100L)
    Assert.False(File.Exists file)
    // The previous log.1 is replaced.
    Assert.Equal(String('a', 100) + "b", File.ReadAllText(file + ".1"))

    // A log that is not there yet is no error.
    Assert.False(Log.rollOver file 100L)

[<Fact>]
let ``several writers at the same time lose no lines`` () =
    let file = tempLog ()
    let writers = 8
    let lines = 200

    // Each line opens the file again, as a line of another process does.
    Parallel.For(0, writers, fun w ->
        for i in 1..lines do
            Log.withFileLock file (fun () -> Log.append file $"writer {w} line {i}{Environment.NewLine}"))
    |> ignore

    let written = File.ReadAllLines file
    Assert.Equal(writers * lines, written.Length)
    Assert.Equal(writers * lines, (set written).Count)

[<Fact>]
let ``the log can be written while another process has it open`` () =
    let file = tempLog ()
    File.WriteAllText(file, "first" + Environment.NewLine)

    // As eng\Probe-Debugger.ps1 reads it.
    use reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    Log.withFileLock file (fun () -> Log.append file ("second" + Environment.NewLine))

    use text = new StreamReader(reader)
    Assert.Equal<string[]>([| "first"; "second" |], text.ReadToEnd().Split([| Environment.NewLine |], StringSplitOptions.RemoveEmptyEntries))

[<Fact>]
let ``verbose lines are not made without FSHARP_EE_LOG_VERBOSE`` () =
    if not Log.isVerbose.Value then
        Log.verbose "test" (fun () -> failwith "made a verbose line")
