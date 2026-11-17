# Drives the real Windows window against the stand-in proxy through UI Automation
# and saves a screenshot of each state. Run with Windows PowerShell 5.1 after
# .\build.ps1 -Targets win-x64 -Test :
#
#   powershell -ExecutionPolicy Bypass -File tests\windows\ui-demo.ps1 -OutDir <folder>
#
# Checks: idle layout, connect, crash -> reconnect, a second copy picking the
# next saved connection, the "port in use" hint and warning, and that killing
# the app also kills its proxy.

param([Parameter(Mandatory = $true)][string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tests = Join-Path $root 'dist\tests-win-x64'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    public static void MoveCursor(int x, int y) { SetCursorPos(x, y); }
    public static void Save(IntPtr hwnd, string path) {
        RECT r; GetWindowRect(hwnd, out r);
        using (Bitmap bmp = new Bitmap(r.R - r.L, r.B - r.T)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                IntPtr dc = g.GetHdc();
                PrintWindow(hwnd, dc, 2 /*PW_RENDERFULLCONTENT*/);
                g.ReleaseHdc(dc);
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    public static void Screen(IntPtr hwnd, string path) {
        RECT r; GetWindowRect(hwnd, out r);
        using (Bitmap bmp = new Bitmap(r.R - r.L, r.B - r.T)) {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.L, r.T, 0, 0, bmp.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@
[void][Shot]::SetProcessDPIAware()

$A = [System.Windows.Automation.AutomationElement]
$results = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) {
    $line = $(if ($ok) { '  ok    ' } else { '  FAIL  ' }) + $what
    $results.Add($line); Write-Output $line
}
function Get-Window($proc) {
    for ($i = 0; $i -lt 120; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $A::FromHandle($proc.MainWindowHandle) }
        Start-Sleep -Milliseconds 250
    }
    throw 'The window did not appear.'
}
function Find($win, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($el -eq $null) { throw "Control '$id' not found." }
    return $el
}
function Text($win, [string]$id) { (Find $win $id).Current.Name }
function Press($win, [string]$id) { (Find $win $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-Text($win, [string]$id, [string]$expected, [int]$seconds) {
    for ($i = 0; $i -lt $seconds * 10; $i++) {
        if ((Text $win $id) -eq $expected) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}
function Fake-Pids { @(Get-Process -Name 'fakeproxy' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) }
function Port-Free([int]$p) {
    -not ([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object { $_.Port -eq $p })
}
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class Win {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static bool Visible(IntPtr h) { return IsWindowVisible(h); }
    // Visible top-level windows of a process with the given title, other than one handle.
    public static List<IntPtr> Titled(int pid, string title, IntPtr except) {
        var r = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && h != except && IsWindowVisible(h)) {
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                if (t.ToString() == title) r.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return r;
    }
}
'@
function Dialog($proc, [IntPtr]$mainHwnd) {
    # The warning is a second top-level window with the app's title; UI Automation's
    # root search does not list Avalonia's owned windows, so find it through Win32.
    $h = [Win]::Titled($proc.Id, 'Cloud SQL Proxy Manager', $mainHwnd) | Select-Object -First 1
    if ($h) { return $A::FromHandle([IntPtr]$h) }
    return $null
}

# --- A throwaway portable folder: app + saved connections; the stand-in proxy via the override variable ---
$demo = Join-Path $OutDir 'app'
New-Item -ItemType Directory -Force $demo | Out-Null
Copy-Item (Join-Path $root 'dist\win-x64\CloudSqlProxyManager.exe') $demo -Force
$env:CLOUDSQLPROXY_EXE = Join-Path $tests 'fakeproxy.exe'
if (-not (Test-Path $env:CLOUDSQLPROXY_EXE)) { throw "Stand-in proxy missing: $env:CLOUDSQLPROXY_EXE (run .\build.ps1 -Targets win-x64 -Test)" }

$portA = 3306; while (-not (Port-Free $portA) -or -not (Port-Free ($portA + 1))) { $portA += 10 }
$portB = $portA + 1
$key = Join-Path $demo 'demo-key.json'
'{"type":"service_account","project_id":"my-project","client_email":"sql-reader@my-project.iam.gserviceaccount.com"}' | Set-Content -Path $key -Encoding ASCII
$keyJson = $key.Replace('\', '\\')
function Seed-Settings([bool]$hide) {
    $h = if ($hide) { 'true' } else { 'false' }
@"
{"ProxyPath":"","AutoReconnect":true,"HideOnConnect":$h,"Connections":[
 {"Project":"my-project","Region":"asia-southeast2","Instance":"orders-db-replica-1","Port":$portA,"CredentialsFile":"$keyJson","ExtraArgs":""},
 {"Project":"my-project","Region":"asia-southeast2","Instance":"orders-db-replica-2","Port":$portB,"CredentialsFile":"$keyJson","ExtraArgs":""}]}
"@ | Set-Content -Path (Join-Path $demo 'CloudSqlProxyManager.settings.json') -Encoding ASCII
}
# The window must stay visible for the screenshots; hide-on-connect gets its own check at the end.
Seed-Settings $false

$exe = Join-Path $demo 'CloudSqlProxyManager.exe'
$app1 = $null; $app2 = $null
$ellipsis = [string][char]0x2026
function Combo-Text($win, [string]$id) { (Find $win ($id + 'Text')).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Combo-Set($win, [string]$id, [string]$value) { (Find $win ($id + 'Text')).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Combo-Choices($win, [string]$id) { [int](((Find $win ($id + 'Drop')).Current.Name) -replace '\D', '') }
try {
    # 0. First run, nothing saved yet: the known project with its region filled in,
    #    and the three fields kept in step as the project is retyped
    $fresh = Join-Path $OutDir 'fresh'
    New-Item -ItemType Directory -Force $fresh | Out-Null
    Remove-Item (Join-Path $fresh '*.json') -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root 'dist\win-x64\CloudSqlProxyManager.exe') $fresh -Force
    $app0 = Start-Process (Join-Path $fresh 'CloudSqlProxyManager.exe') -PassThru
    try {
        $w0 = Get-Window $app0
        Start-Sleep -Milliseconds 1500
        $project = Combo-Text $w0 'ProjectBox'; $region = Combo-Text $w0 'RegionBox'
        Check ($project -ne '') "first run starts on the known project: $project"
        Check ($region -ne '') "its region is filled in: $region"
        Check ((Combo-Text $w0 'InstanceBox') -eq '') 'instance is left for the user to choose'
        $instances = Combo-Choices $w0 'InstanceBox'; $regions = Combo-Choices $w0 'RegionBox'
        Check ($instances -ge 1 -and $regions -lt 10) "the project's own regions ($regions) and instances ($instances) are offered"
        [Shot]::Save($app0.MainWindowHandle, (Join-Path $OutDir '0-first-run.png'))
        # The open list is a popup window of its own: capture the screen around the main window.
        (Find $w0 'InstanceBoxDrop').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700
        [Shot]::Screen($app0.MainWindowHandle, (Join-Path $OutDir '0b-first-run-list.png'))
        (Find $w0 'InstanceBoxDrop').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        # Hover over the chevron and capture the hover state.
        $rect = (Find $w0 'InstanceBoxDrop').Current.BoundingRectangle
        [Shot]::MoveCursor([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
        Start-Sleep -Milliseconds 600
        [Shot]::Screen($app0.MainWindowHandle, (Join-Path $OutDir '0c-chevron-hover.png'))
        [Shot]::MoveCursor(5, 5)

        Combo-Set $w0 'ProjectBox' 'some-other-project'
        Start-Sleep -Milliseconds 500
        Check ((Combo-Choices $w0 'InstanceBox') -eq 0 -and (Combo-Choices $w0 'RegionBox') -gt 10) 'an unknown project: no known instances offered, any region allowed'
        Combo-Set $w0 'ProjectBox' $project
        Start-Sleep -Milliseconds 500
        Check ((Combo-Choices $w0 'InstanceBox') -eq $instances -and (Combo-Text $w0 'RegionBox') -eq $region) 'back on the known project: its instances and region are back'
        Combo-Set $w0 'InstanceBox' 'typed-by-hand'
        Combo-Set $w0 'RegionBox' 'us-central1'
        Start-Sleep -Milliseconds 500
        Check ((Combo-Text $w0 'InstanceBox') -eq '' -and (Combo-Choices $w0 'InstanceBox') -eq 0) 'a region where that instance does not live clears the instance'
        Combo-Set $w0 'InstanceBox' 'typed-by-hand'
        Start-Sleep -Milliseconds 300
        Check ((Text $w0 'DetailText') -eq "$project`:us-central1:typed-by-hand  " + [char]0x2192 + "  127.0.0.1:3306") "free-typed names are accepted: $(Text $w0 'DetailText')"
    }
    finally { Stop-Process -Id $app0.Id -Force }

    # 1. Idle
    $app1 = Start-Process $exe -PassThru
    $w1 = Get-Window $app1
    Start-Sleep -Milliseconds 1500
    [Shot]::Save($app1.MainWindowHandle, (Join-Path $OutDir '1-idle.png'))
    Check ((Text $w1 'StateText') -eq 'Disconnected') 'opens Disconnected'
    Check ((Text $w1 'PortHint') -eq "Port $portA is free.") "port hint: $(Text $w1 'PortHint')"

    # 2. Connect
    Press $w1 'ActionButton'
    Check (Wait-Text $w1 'StateText' 'Connected' 10) 'Connect -> Connected'
    Start-Sleep -Milliseconds 800
    [Shot]::Save($app1.MainWindowHandle, (Join-Path $OutDir '2-connected.png'))
    $first = Fake-Pids
    Check ($first.Count -eq 1) 'one proxy process running'
    Check ((Text $w1 'PortHint') -eq "Port $portA is used by this tunnel.") "port hint while connected: $(Text $w1 'PortHint')"
    $app1.Refresh()
    Check ($app1.MainWindowTitle -like "orders-db-replica-1*:$portA*Connected*") "window title: $($app1.MainWindowTitle -replace '[^\x20-\x7E]', '?')"

    # 3. Crash -> reconnect
    Stop-Process -Id $first[0] -Force
    Check (Wait-Text $w1 'StateText' "Reconnecting$ellipsis" 3) 'proxy killed -> Reconnecting'
    [Shot]::Save($app1.MainWindowHandle, (Join-Path $OutDir '3-reconnecting.png'))
    Check (Wait-Text $w1 'StateText' 'Connected' 10) 'back to Connected by itself'
    Start-Sleep -Milliseconds 800
    [Shot]::Save($app1.MainWindowHandle, (Join-Path $OutDir '4-reconnected.png'))
    $second = Fake-Pids
    Check ($second.Count -eq 1 -and $second[0] -ne $first[0]) 'a new proxy process replaced the dead one'

    # 4. Second copy of the app
    $app2 = Start-Process $exe -PassThru
    $w2 = Get-Window $app2
    $h2 = $app2.MainWindowHandle
    Start-Sleep -Milliseconds 1500
    Check ((Text $w2 'DetailText') -like '*orders-db-replica-2*') "second copy opens on the next saved connection: $(Text $w2 'DetailText')"
    Check ((Text $w2 'PortHint') -eq "Port $portB is free.") "second copy port hint: $(Text $w2 'PortHint')"
    Press $w2 'ActionButton'
    Check (Wait-Text $w2 'StateText' 'Connected' 10) 'second copy connects on its own port'
    Check ((Fake-Pids).Count -eq 2) 'two proxies running side by side'
    Start-Sleep -Milliseconds 800
    [Shot]::Save($app2.MainWindowHandle, (Join-Path $OutDir '5-second-copy-connected.png'))

    # 5. Port clash: disconnect copy 2, then pick copy 1's saved connection (port A)
    Press $w2 'ActionButton'
    Check (Wait-Text $w2 'StateText' 'Disconnected' 5) 'second copy disconnects'
    Start-Sleep -Milliseconds 800
    Check ((Fake-Pids).Count -eq 1 -and (Fake-Pids)[0] -eq $second[0]) 'only its own proxy was stopped'
    $saved = Find $w2 'SavedBox'
    $saved.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    $items = $saved.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $target = $items | Where-Object { $_.Current.Name -like 'orders-db-replica-1*' } | Select-Object -First 1
    $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    try { $saved.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    Start-Sleep -Milliseconds 2600
    $hint = Text $w2 'PortHint'
    Check ($hint -like "Port $portA is in use by fakeproxy.exe (PID $($second[0])).") "clash hint: $hint"
    Check ((Text $w2 'PortSuggest') -eq "Use $portB") "suggests the next free port: $(Text $w2 'PortSuggest')"
    [Shot]::Save($app2.MainWindowHandle, (Join-Path $OutDir '6-port-in-use.png'))

    Press $w2 'ActionButton'
    $dialog = $null
    for ($i = 0; $i -lt 30 -and $dialog -eq $null; $i++) { Start-Sleep -Milliseconds 200; $dialog = Dialog $app2 $h2 }
    Check ($dialog -ne $null) 'Connect on a busy port is refused with a warning'
    if ($dialog -ne $null) {
        $msg = ($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
            ForEach-Object { $_.Current.Name }) -join ' '
        Write-Output ("        warning text: " + ($msg -replace '\s+', ' '))
        [Shot]::Save([IntPtr]$dialog.Current.NativeWindowHandle, (Join-Path $OutDir '7-port-warning.png'))
        $ok = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
        $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
    }
    Check ((Text $w2 'StateText') -eq 'Disconnected' -and (Fake-Pids).Count -eq 1) 'nothing was started on the busy port'

    # 6. Killing the app must not leave its proxy behind
    Stop-Process -Id $app1.Id -Force
    $gone = $false
    for ($i = 0; $i -lt 30 -and -not $gone; $i++) { Start-Sleep -Milliseconds 100; $gone = (Fake-Pids).Count -eq 0 }
    Check $gone 'killing the app also ends its proxy (no orphan holding the port)'
    Stop-Process -Id $app2.Id -Force; $app2 = $null
    Start-Sleep -Milliseconds 800

    # 7. Hide on connect (the default): the window leaves the screen once the tunnel is up
    Seed-Settings $true
    $app1 = Start-Process $exe -PassThru
    $w1 = Get-Window $app1
    $h1 = $app1.MainWindowHandle
    Start-Sleep -Milliseconds 1200
    Check ((Find $w1 'HideBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') 'hide-on-connect option is ticked'
    Press $w1 'ActionButton'
    $hidden = $false
    for ($i = 0; $i -lt 50 -and -not $hidden; $i++) { Start-Sleep -Milliseconds 200; $hidden = -not [Win]::Visible($h1) }
    Check $hidden 'window hides itself once connected'
    Check ((Fake-Pids).Count -eq 1) 'the tunnel keeps running while hidden'
}
finally {
    foreach ($p in @($app1, $app2)) {
        if ($p -ne $null) { try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { } }
    }
    Start-Sleep -Milliseconds 500
    Get-Process -Name 'fakeproxy' -ErrorAction SilentlyContinue | Stop-Process -Force
}

$failed = @($results | Where-Object { $_ -like '  FAIL*' }).Count
Write-Output ''
Write-Output ("{0} checks, {1} failed" -f $results.Count, $failed)
exit $failed
