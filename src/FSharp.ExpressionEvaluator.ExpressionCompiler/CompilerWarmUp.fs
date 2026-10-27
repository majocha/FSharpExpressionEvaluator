namespace FSharp.ExpressionEvaluator

open System
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.ComponentInterfaces

/// Starts the compiler's warm-up when a debuggee loads FSharp.Core (docs/DESIGN.md §6.9): F# code
/// is about to run, and its first break usually comes later. The warm-up runs on a thread-pool
/// thread, once per devenv process; this only starts it.
/// FSHARP_EE_COMPILER_WARMUP=0 turns it off, to measure without it.
type CompilerWarmUp() =

    static let off = Environment.GetEnvironmentVariable "FSHARP_EE_COMPILER_WARMUP" = "0"

    interface IDkmModuleInstanceLoadNotification with
        member _.OnModuleInstanceLoad(moduleInstance, _workList, _eventDescriptor) =
            try
                if
                    not off
                    && String.Equals(moduleInstance.Name, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase)
                    && Settings.enabled ()
                then
                    Debuggee.warmUpCompiler ()
            with e ->
                Log.exn "WU" "compiler warm-up" e
