param([string]$Name = 'KMUTTVPN', [string]$Shot, [switch]$ListOnly, [switch]$Close)
# Click "Connect" for a VPN profile in Windows Settings (rasdial fails with error 645 on this PC).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$st = (Get-VpnConnection -AllUserConnection -Name $Name -ErrorAction SilentlyContinue).ConnectionStatus
if (-not $st) { $st = (Get-VpnConnection -Name $Name -ErrorAction SilentlyContinue).ConnectionStatus }
"status before: $st"
if ($st -eq 'Connected' -and -not $ListOnly) { return }

Start-Process 'ms-settings:network-vpn'
Start-Sleep -Seconds 4
$root = [Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 20 -and -not $win; $i++) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Settings')
    $win = $root.FindFirst([Windows.Automation.TreeScope]::Children, $c)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { throw 'Settings window not found' }
Start-Sleep -Seconds 2

function Get-All($el) {
    $el.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
}
$all = Get-All $win
$items = foreach ($e in $all) {
    $n = $e.Current.Name; $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($n -and ($n -match [regex]::Escape($Name) -or $n -match '^(Connect|Disconnect|Cancel|Advanced options)$' -or $t -eq 'Edit')) {
        [pscustomobject]@{ Type = $t; Name = $n; Id = $e.Current.AutomationId; El = $e }
    }
}
$items | Select-Object Type, Name, Id | Format-Table -AutoSize | Out-String

if (-not $ListOnly) {
    $btn = $items | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Connect' } | Select-Object -First 1
    if (-not $btn) {
        # the profile row may need expanding first
        $row = $items | Where-Object { $_.Name -match [regex]::Escape($Name) } | Select-Object -First 1
        if ($row) {
            $p = $null
            if ($row.El.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand() }
            elseif ($row.El.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
            Start-Sleep -Seconds 2
            $all = Get-All $win
            foreach ($e in $all) { if ($e.Current.Name -eq 'Connect' -and $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $btn = [pscustomobject]@{ El = $e }; break } }
        }
    }
    if (-not $btn) { throw 'Connect button not found' }
    $p = $null
    if ($btn.El.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); 'clicked Connect' }
    else { throw 'Connect button cannot be invoked' }
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $st = (Get-VpnConnection -AllUserConnection -Name $Name -ErrorAction SilentlyContinue).ConnectionStatus
        if (-not $st) { $st = (Get-VpnConnection -Name $Name -ErrorAction SilentlyContinue).ConnectionStatus }
        if ($st -eq 'Connected') { break }
    }
    "status after: $st"
}
if ($Shot) {
    $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    $gr = [Drawing.Graphics]::FromImage($bmp); $gr.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $gr.Dispose()
    $bmp.Save($Shot); $bmp.Dispose(); "shot: $Shot"
}
if ($Close) {
    $p = $null
    if ($win.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { $p.Close(); 'settings closed' }
}
