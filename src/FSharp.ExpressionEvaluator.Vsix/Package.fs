namespace FSharp.ExpressionEvaluator

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Utilities.UnifiedSettings

// The service is documented, but its marker type is in VS's internal interop assembly.
[<Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")>]
type internal SVsUnifiedSettingsManager = interface end

module internal PackageIdentity =
    [<Literal>]
    let Guid = "c11fa5ec-6e38-4966-85ad-c715925ec7a0"

    [<Literal>]
    let EnabledSetting = "debugging.fsharpExpressionEvaluator.enabled"

/// Registers the option (registration.json) and mirrors it to the switch the Concord components
/// read (Settings.fs), at startup and on every change, so it applies without restarting VS.
[<Guid(PackageIdentity.Guid)>]
[<PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)>]
[<ProvideSettingsManifest>]
[<ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)>]
type ExpressionEvaluatorPackage() =
    inherit AsyncPackage()

    static let log = Log.write "PK"

    let mutable subscription: IDisposable = null

    let mirror (reader: ISettingsReader) =
        try
            let result = reader.GetValue<bool>(PackageIdentity.EnabledSetting, SettingReadOptions.RequireRegistration)

            if result.Outcome = SettingRetrievalOutcome.Success then
                Settings.setEnabled result.Value
                log $"F# EE enabled: {result.Value}"
            else
                log $"Cannot read '{PackageIdentity.EnabledSetting}': {result.Outcome}. {result.Message}"
        with e ->
            Log.exn "PK" "mirror" e

    override this.InitializeAsync(cancellationToken: CancellationToken, _progress: IProgress<ServiceProgressData>) : Task =
        task {
            match! this.GetServiceAsync(typeof<SVsUnifiedSettingsManager>) with
            | :? ISettingsManager as manager ->
                let reader = manager.GetReader()
                mirror reader
                subscription <- reader.SubscribeToChanges(Action<SettingsUpdate>(fun _ -> mirror reader), PackageIdentity.EnabledSetting)
            | _ -> log "Unified Settings service unavailable"
        }

    override _.Dispose(disposing: bool) =
        if disposing && not (isNull subscription) then
            subscription.Dispose()

        base.Dispose disposing
