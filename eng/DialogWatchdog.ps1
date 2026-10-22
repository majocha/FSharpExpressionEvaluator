<#
.SYNOPSIS
  Clicks OK on the debugger's informational "To prevent an unsafe abort ... all threads were allowed to run"
  dialog in a VS process, which otherwise blocks every DTE call. Logs each occurrence (it is a finding:
  func-eval of F# %A display needed all threads). Runs until the VS process exits or -Once is satisfied.
#>
param(
    [Parameter(Mandatory)] [int] $VsPid,
    [string] $Log,
    [switch] $Once
)

Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class DialogWatchdog {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    const uint BM_CLICK = 0x00F5;

    static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }
    static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }

    /// Returns the dialog message if one was dismissed, otherwise null.
    public static string DismissUnsafeAbortDialog(uint pid) {
        string dismissed = null;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h) || Class(h) != "#32770") return true;
            string message = null; IntPtr ok = IntPtr.Zero;
            EnumChildWindows(h, (c, l2) => {
                var text = Text(c);
                if (Class(c) == "Static" && text.Contains("unsafe abort")) message = text;
                if (Class(c) == "Button" && text == "OK") ok = c;
                return true;
            }, IntPtr.Zero);
            if (message != null && ok != IntPtr.Zero) { SendMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero); dismissed = message; }
            return true;
        }, IntPtr.Zero);
        return dismissed;
    }
}
'@

while (Get-Process -Id $VsPid -ErrorAction SilentlyContinue) {
    $message = [DialogWatchdog]::DismissUnsafeAbortDialog($VsPid)
    if ($message) {
        $line = "$(Get-Date -Format 'HH:mm:ss.fff') [watchdog] dismissed: $message"
        Write-Host $line
        if ($Log) { Add-Content -Path $Log -Value $line }
        if ($Once) { break }
    }
    Start-Sleep -Milliseconds 500
}
