<#
    Presses the real Benchmix buttons — Match BPM, Reset, Match key — and checks what they did,
    by reading the real labels back.

    This exists because "it compiles and the unit tests pass" says nothing about whether clicking
    a button in the actual window does the right thing.

    Until 2026-09-24 this script drove the buttons from outside with UI Automation. Watched from
    outside, that turned out to make Windows ACTIVATE the hidden window at the very first UI
    Automation call, taking the keyboard from whoever was working for as long as the run lasted —
    even though the window cannot be activated any other way. So the same presses now happen
    inside the app (--buttontest), through each button's own automation peer, which is the object
    an outside client ends up calling anyway. Nothing reaches across processes, and nothing is
    activated. This script launches that run quietly and watches it from outside (Intrusion.cs).

    Usage:  powershell -ExecutionPolicy Bypass -File scripts\ui-verify.ps1 [-Exe <path>]
    Exit:   0 = all checks passed and it stayed out of the way, 1 = something failed,
            2 = incomplete: the sample songs are missing
#>
param([string]$Exe = "$env:LOCALAPPDATA\Benchmix.build\publish\Benchmix.App\release_win-x64\Benchmix.exe")

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { Write-Host "No exe at $Exe - publish first."; exit 1 }

# A build from before scripted runs were invisible would open on screen and take focus.
$repo = Split-Path $PSScriptRoot
$newest = @(Get-ChildItem (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj, *.manifest) +
          @(Get-Item (Join-Path $repo 'Directory.Build.props')) | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest.LastWriteTime -gt (Get-Item $Exe).LastWriteTime) {
    Write-Host "The exe is older than $($newest.FullName.Substring($repo.Length + 1)) - publish first. Nothing was launched."
    exit 1
}

$audio = "$env:USERPROFILE\Music\Sports DJ\audio"
$bed = Join-Path $audio "CFBCA39E-E325-485F-839D-CF95466767AA.mp3"   # Still D.R.E.  93 BPM G#m
$top = Join-Path $audio "9523F493-5F99-4F39-9784-0C129F7286A7.mp3"   # T.R.O.Y.     102 BPM G#m
if (-not (Test-Path $bed) -or -not (Test-Path $top)) { Write-Host "INCOMPLETE: sample library not present"; exit 2 }

Add-Type -Path (Join-Path $PSScriptRoot 'Intrusion.cs')
$log = "$env:LOCALAPPDATA\Benchmix\benchmix.log"
$before = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }

$p = New-Object QuietProcess $Exe, "--buttontest `"$bed`" `"$top`""
$watch = New-Object IntrusionWatch $p.Id
if (-not $p.WaitForExit(120000)) { $p.Kill(); Write-Host "  FAIL  it did not finish" }
$watch.Stop()
$exit = $p.ExitCode
$p.Dispose()

$lines = if (Test-Path $log) { Get-Content $log | Select-Object -Skip $before } else { @() }
$lines | Select-String 'buttontest: (PASS|FAIL) - ' | ForEach-Object { Write-Host ("  " + ($_.Line.Substring(26) -replace '^(PASS|FAIL) - ', '$1  ')) }
$inside = ($lines | Select-String -SimpleMatch 'scripted run: off-screen=' | ForEach-Object { $_.Line.Substring(14) }) -join ' | '

Write-Host "`nStayed out of the way"
Write-Host "  outside: $($watch.Describe())"
Write-Host "  inside:  $(if ($inside) { $inside } else { 'MISSING - the run never checked itself' })"

$ok = $exit -eq 0 -and -not $watch.GotInTheWay -and $inside -ne ''
Write-Host ""
if ($ok) { Write-Host "UI VERIFY: all checks passed, and it stayed out of the way"; exit 0 }
Write-Host "UI VERIFY: failed (exit $exit$(if ($watch.GotInTheWay) { ', and it got in the way' }))"; exit 1
