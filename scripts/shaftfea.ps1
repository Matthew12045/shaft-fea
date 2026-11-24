param(
    [ValidateSet('status', 'build', 'prepare', 'inspect', 'solve', 'sweep', 'selftest', 'close', 'finish')] [string]$Action = 'status',
    [string]$Cad,              # inspect: the shaft CAD (.SLDPRT, .STEP, .IGS, .X_T)
    [string]$Case,             # solve / sweep: the case file (JSON)
    [string]$Factors,          # sweep: mesh sizes as multiples of the default element size, e.g. "1,0.75,0.5" (the last one stays saved)
    [string]$Out,              # inspect: also write the geometry as JSON here
    [string]$Dir,              # selftest: folder for its files (default: %TEMP%\shaft-fea-selftest)
    [switch]$NoVpn,            # prepare: do not touch the VPN
    [switch]$KeepVpn,          # finish: leave the VPN connected
    [switch]$Force             # close / finish: act even if this script did not start SOLIDWORKS or connect the VPN
)
# Session helper for the shaft-fea skill (SOLIDWORKS Simulation). See ..\SKILL.md.
#   status    what is running and whether the tools are built
#   build     compile the tools into scripts\bin (done automatically by prepare)
#   prepare   build, connect the VPN the licence needs (if machine.json names one), start SOLIDWORKS and wait until it answers
#   inspect   describe a shaft CAD: axis, ends, diameters along the axis, keyways. Changes nothing.
#   solve     run a case file: copy the CAD, build the study, mesh, solve, report, save pictures
#   sweep     the same study at several mesh sizes, for a convergence check
#   selftest  a plain bar against beam theory: proves the tool, SOLIDWORKS and the licence work
#   close     close every open SOLIDWORKS document without saving; SOLIDWORKS stays open
#   finish    close SOLIDWORKS without saving what is still open, and disconnect the VPN
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Skill = Split-Path -Parent $PSScriptRoot
$Bin = Join-Path $PSScriptRoot 'bin'
$Exe = Join-Path $Bin 'ShaftFea.exe'
$StateDir = Join-Path $env:LOCALAPPDATA 'shaft-fea'
$StateFile = Join-Path $StateDir 'session.json'
New-Item -ItemType Directory -Force $StateDir | Out-Null
$cfgFile = Join-Path $Skill 'machine.json'
$Cfg = if (Test-Path -LiteralPath $cfgFile) { Get-Content -LiteralPath $cfgFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$SwExe = if ($Cfg.solidworksExe) { $Cfg.solidworksExe } else { 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe' }
$Interop = if ($Cfg.interopDir) { $Cfg.interopDir } else { Join-Path (Split-Path -Parent $SwExe) 'api\redist' }
$VpnName = if ($Cfg.vpn -and $Cfg.vpn.name) { $Cfg.vpn.name } else { $null }

function Get-Vpn {
    if (-not $VpnName) { return 'none configured' }
    $st = (Get-VpnConnection -AllUserConnection -Name $VpnName -ErrorAction SilentlyContinue).ConnectionStatus
    if (-not $st) { $st = (Get-VpnConnection -Name $VpnName -ErrorAction SilentlyContinue).ConnectionStatus }
    if ($st) { "$st" } else { 'profile not found' }
}
function Get-State { if (Test-Path -LiteralPath $StateFile) { Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json } else { $null } }
function Save-State($s) { ($s | ConvertTo-Json) | Set-Content -LiteralPath $StateFile -Encoding UTF8 }
# PowerShell cannot call the SOLIDWORKS COM object itself (TYPE_E_ELEMENTNOTFOUND), so readiness and the list of open
# documents come from a small compiled helper: exit code 0 = ready.
function Get-SwInfo {
    if (-not (Test-Path -LiteralPath (Join-Path $Bin 'SwDocs.exe'))) { return [pscustomobject]@{ Ready = $false; Lines = @('tools not built') } }
    $o = @(& (Join-Path $Bin 'SwDocs.exe')); [pscustomobject]@{ Ready = ($LASTEXITCODE -eq 0); Lines = $o }
}
function Build-Tools {
    New-Item -ItemType Directory -Force $Bin | Out-Null
    $dlls = 'SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll', 'SolidWorks.Interop.cosworks.dll'
    foreach ($d in $dlls) {
        if (-not (Test-Path -LiteralPath (Join-Path $Bin $d))) {
            $src = Join-Path $Interop $d
            if (-not (Test-Path -LiteralPath $src)) { throw "SOLIDWORKS interop library not found: $src (set interopDir in machine.json)" }
            Copy-Item -LiteralPath $src -Destination $Bin
        }
    }
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64' -Directory | Sort-Object Name | ForEach-Object { Join-Path $_.FullName 'csc.exe' } | Where-Object { Test-Path $_ } | Select-Object -Last 1
    if (-not $csc) { throw 'no .NET Framework C# compiler (csc.exe) found' }
    $jobs = @(
        @{ Name = 'ShaftFea'; Refs = $dlls + 'System.Web.Extensions.dll' },
        @{ Name = 'SwDocs'; Refs = @($dlls[0]) },
        @{ Name = 'CloseNoSave'; Refs = @($dlls[0]) })
    Push-Location $Bin
    try {
        foreach ($j in $jobs) {
            $cs = Join-Path $PSScriptRoot "$($j.Name).cs"; $exe = Join-Path $Bin "$($j.Name).exe"
            if ((Test-Path -LiteralPath $exe) -and (Get-Item -LiteralPath $exe).LastWriteTime -ge (Get-Item -LiteralPath $cs).LastWriteTime) { continue }
            $a = @('/nologo', '/platform:x64', "/out:$($j.Name).exe") + ($j.Refs | ForEach-Object { "/r:$_" }) + @($cs)
            $o = & $csc @a
            $o | Where-Object { $_ -notmatch 'CS0649' }
            if ($LASTEXITCODE -ne 0) { throw "$($j.Name).cs did not compile" }
            "compiled $($j.Name).exe"
        }
    } finally { Pop-Location }
}
function Assert-Ready {
    $i = Get-SwInfo
    if (-not $i.Ready) { throw 'SOLIDWORKS is not running or not ready. Run -Action prepare first.' }
}
function Invoke-Tool([string[]]$a) {
    & $Exe @a
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }
}

switch ($Action) {
    'status' {
        "VPN: " + $(if ($VpnName) { "$VpnName " + (Get-Vpn) } else { 'none configured in machine.json' })
        $p = Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { "SOLIDWORKS: running (pid $($p.Id), since $($p.StartTime))"; $i = Get-SwInfo; if ($i.Ready) { $i.Lines | ForEach-Object { "  $_" } } else { '  not ready for commands yet (still starting, a start-up dialog is open, or the tools are not built)' } }
        else { 'SOLIDWORKS: not running' }
        $st = Get-State
        if ($st) { "last prepare: $($st.time)  started SOLIDWORKS: $($st.startedSw)  connected VPN: $($st.connectedVpn)" }
        foreach ($n in 'ShaftFea', 'SwDocs', 'CloseNoSave') {
            $exe = Join-Path $Bin "$n.exe"
            if (Test-Path -LiteralPath $exe) { "{0}.exe built {1:yyyy-MM-dd HH:mm}{2}" -f $n, (Get-Item -LiteralPath $exe).LastWriteTime, $(if ((Get-Item -LiteralPath (Join-Path $PSScriptRoot "$n.cs")).LastWriteTime -gt (Get-Item -LiteralPath $exe).LastWriteTime) { '  (source is newer: run -Action build)' } else { '' }) }
            else { "$n.exe not built yet (-Action build or prepare)" }
        }
    }

    'build' { Build-Tools; 'tools are up to date' }

    'prepare' {
        Build-Tools
        $state = [ordered]@{ time = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); startedSw = $false; connectedVpn = $false; swPid = 0 }
        $p = Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) {
            $old = Get-State
            if ($old -and $old.swPid -eq $p.Id) { $state.startedSw = [bool]$old.startedSw; $state.connectedVpn = [bool]$old.connectedVpn }
            "SOLIDWORKS is already running (pid $($p.Id)); started by an earlier prepare: $($state.startedSw)"
        } else {
            if ($VpnName -and -not $NoVpn) {
                $vpn = Get-Vpn
                "VPN ${VpnName}: $vpn"
                if ($vpn -ne 'Connected') {
                    # on this PC rasdial cannot connect the profile (error 645), so Connect is clicked in Windows Settings
                    & (Join-Path $PSScriptRoot 'vpn_connect.ps1') -Name $VpnName -Close | ForEach-Object { "  vpn: $_" }
                    $vpn = Get-Vpn
                    if ($vpn -ne 'Connected') { throw "VPN $VpnName did not connect (status: $vpn). SOLIDWORKS cannot get its licence without it." }
                    $state.connectedVpn = $true
                }
            }
            if (-not (Test-Path -LiteralPath $SwExe)) { throw "SOLIDWORKS not found at $SwExe (set solidworksExe in machine.json)" }
            $state.swPid = (Start-Process $SwExe -PassThru).Id
            $state.startedSw = $true
            Save-State $state   # written before the wait, so "finish" still knows who started what if the wait fails
            'SOLIDWORKS starting ...'
        }
        $info = $null
        for ($i = 0; $i -lt 80; $i++) { $info = Get-SwInfo; if ($info.Ready) { break }; Start-Sleep -Seconds 5 }
        if (-not $info.Ready) { throw 'SOLIDWORKS did not become ready in 400 s. Look at the screen: a licence or start-up dialog may be waiting.' }
        Start-Sleep -Seconds 8
        $state.swPid = (Get-Process SLDWORKS | Select-Object -First 1).Id
        Save-State $state
        'SOLIDWORKS ready: ' + ($info.Lines -join '; ')
        if (-not $state.startedSw) { 'NOTE: SOLIDWORKS was open before this session. "close" and "finish" leave it alone unless -Force is given; check that none of the open documents belong to the user.' }
    }

    'inspect' {
        if (-not $Cad) { throw 'give -Cad <path to the shaft CAD>' }
        if (-not (Test-Path -LiteralPath $Cad)) { throw "CAD file not found: $Cad" }
        Assert-Ready
        $a = @('inspect', (Resolve-Path -LiteralPath $Cad).Path); if ($Out) { $a += $Out }
        Invoke-Tool $a
    }

    { $_ -in 'solve', 'sweep' } {
        if (-not $Case) { throw 'give -Case <path to the case file>' }
        if (-not (Test-Path -LiteralPath $Case)) { throw "case file not found: $Case" }
        Assert-Ready
        $a = @($Action, (Resolve-Path -LiteralPath $Case).Path); if ($Action -eq 'sweep' -and $Factors) { $a += $Factors }
        Invoke-Tool $a
    }

    'selftest' {
        Assert-Ready
        if (-not $Dir) { $Dir = Join-Path $env:TEMP 'shaft-fea-selftest' }
        Invoke-Tool @('selftest', $Dir)
    }

    'close' {
        $st = Get-State
        $p = Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $p) { 'SOLIDWORKS is not running.'; break }
        if (-not ($Force -or ($st -and $st.startedSw -and $st.swPid -eq $p.Id))) { throw 'SOLIDWORKS was not started by "prepare": its documents may be the user''s. They are closed WITHOUT saving, so pass -Force only when that is safe.' }
        & (Join-Path $Bin 'CloseNoSave.exe') keep | ForEach-Object { "  $_" }
    }

    'finish' {
        $st = Get-State
        $p = Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) {
            if ($Force -or ($st -and $st.startedSw -and $st.swPid -eq $p.Id)) {
                & (Join-Path $Bin 'CloseNoSave.exe') | ForEach-Object { "  $_" }
                for ($i = 0; $i -lt 20 -and (Get-Process SLDWORKS -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Seconds 2 }
                if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { 'SOLIDWORKS is still running (a document may have refused to close).' } else { 'SOLIDWORKS closed.' }
            } else { 'SOLIDWORKS was not started by "prepare", so it is left open. Use -Force only when none of the open documents belong to the user (they are closed WITHOUT saving).' }
        } else { 'SOLIDWORKS is not running.' }
        if ($VpnName -and -not $KeepVpn) {
            if ((Get-Vpn) -eq 'Connected') {
                if ($Force -or ($st -and $st.connectedVpn)) { rasdial $VpnName /disconnect | Out-Null; "VPN ${VpnName}: " + (Get-Vpn) }
                else { "VPN $VpnName was connected before `"prepare`", so it is left on (-Force disconnects it)." }
            } else { "VPN ${VpnName}: " + (Get-Vpn) }
        }
        Remove-Item -LiteralPath $StateFile -ErrorAction SilentlyContinue
    }
}
