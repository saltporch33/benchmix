<#
    Runs every verification switch against the published exe, one at a time, and checks from the
    OUTSIDE that none of them got in the way of whoever is using the machine.

    Each run is started without the "working in background" pointer (QuietProcess) and watched on a
    background thread (IntrusionWatch) for as long as it lives. It fails here — whatever its own
    verdict says — if at any moment the foreground window belonged to it, or any of its visible
    windows was on a monitor, would have had a taskbar button, or could have been activated by
    Windows. The app keeps scripted runs off-screen, out of the taskbar, never activatable and free
    of dialogs (MainWindow.KeepOutOfTheWay, OutOfTheWay.cs) and checks the same things from inside;
    this is the independent half.

    Only --playtest opens the audio device, at master zero. Nothing else touches the speakers.

    It refuses to launch an exe older than the source: a build from before scripted runs were
    invisible would open on screen and take focus.

    Usage:  powershell -ExecutionPolicy Bypass -File scripts\verify-exe.ps1 [-Exe <path>] [-ShotDir <dir>] [-Only <name>]
            -Only runs just the named runs (e.g. -Only buttontest), still watched from outside. That
            proves one switch, not the exe: the full set is still the bar.
    Exit:  0 = every run passed and stayed out of the way
            1 = a run failed, or got in the way, or the exe is out of date
            2 = incomplete: the sample songs are missing, so only --latencycheck ran
#>
param(
    [string]$Exe = "$env:LOCALAPPDATA\Benchmix.build\publish\Benchmix.App\release_win-x64\Benchmix.exe",
    [string]$ShotDir = (Join-Path $env:TEMP 'benchmix-verify'),
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { Write-Host "No exe at $Exe - publish first."; exit 1 }

$repo = Split-Path $PSScriptRoot
$newest = @(Get-ChildItem (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj, *.manifest) +
          @(Get-Item (Join-Path $repo 'Directory.Build.props')) | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest.LastWriteTime -gt (Get-Item $Exe).LastWriteTime) {
    Write-Host "The exe is older than $($newest.FullName.Substring($repo.Length + 1)) - publish first. Nothing was launched."
    exit 1
}

Add-Type -Path (Join-Path $PSScriptRoot 'Intrusion.cs')

$audio = "$env:USERPROFILE\Music\Sports DJ\audio"
$bed = Join-Path $audio "CFBCA39E-E325-485F-839D-CF95466767AA.mp3"   # Still D.R.E.  93 BPM
$top = Join-Path $audio "9523F493-5F99-4F39-9784-0C129F7286A7.mp3"   # T.R.O.Y.     102 BPM
$haveSongs = (Test-Path $bed) -and (Test-Path $top)
if (-not $haveSongs) { Write-Host "Sample songs not found under $audio - running only --latencycheck." }

$log = "$env:LOCALAPPDATA\Benchmix\benchmix.log"
New-Item -ItemType Directory -Force $ShotDir | Out-Null

$runs = @( @{ Name = 'latencycheck'; Args = '--latencycheck' } )
if ($haveSongs) {
    $runs += @(
        @{ Name = 'gesturetest';      Args = "--gesturetest `"$bed`"" },
        @{ Name = 'buttontest';       Args = "--buttontest `"$bed`" `"$top`"" },
        @{ Name = 'loadcheck';        Args = "--loadcheck `"$bed`" `"$top`"" },
        @{ Name = 'playtest';         Args = "--playtest 6 `"$bed`" `"$top`"" },
        @{ Name = 'stemtest';         Args = "--stemtest `"$bed`"" },
        @{ Name = 'uiprobe-sections'; Args = "--demosections --uiprobe `"$ShotDir\sections.png`" `"$bed`"" },
        @{ Name = 'uiprobe-editing';  Args = "--demoediting --uiprobe `"$ShotDir\editing.png`" `"$bed`"" }
    )
}

if ($Only.Count -gt 0) {
    $runs = @($runs | Where-Object { $Only -contains $_.Name })
    if ($runs.Count -eq 0) { Write-Host "No run is called $($Only -join ', '). Nothing was launched."; exit 1 }
}

$failed = 0
foreach ($r in $runs) {
    $before = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
    $p = New-Object QuietProcess $Exe, $r.Args
    $watch = New-Object IntrusionWatch $p.Id
    if (-not $p.WaitForExit(240000)) { $p.Kill() }
    $watch.Stop()
    $exit = $p.ExitCode
    $p.Dispose()

    $lines = if (Test-Path $log) { Get-Content $log | Select-Object -Skip $before } else { @() }
    $verdict = ($lines | Select-String 'verdict=|ui probe written|ui probe failed|FATAL|refused' | ForEach-Object { $_.Line.Substring(14) }) -join ' | '
    $inside = ($lines | Select-String -SimpleMatch 'scripted run: off-screen=' | ForEach-Object { $_.Line.Substring(14) }) -join ' | '

    $ok = $exit -eq 0 -and -not $watch.GotInTheWay -and $inside -ne ''
    if (-not $ok) { $failed++ }
    Write-Host ("{0,-17} {1}  exit {2}   outside: {3}" -f $r.Name, $(if ($ok) { 'PASS' } else { 'FAIL' }), $exit, $watch.Describe())
    Write-Host "                  $verdict"
    Write-Host "                  inside: $(if ($inside) { $inside } else { 'MISSING - the run never checked itself' })"
}

Write-Host ""
if ($failed -gt 0) { Write-Host "VERIFY: $failed run(s) failed or got in the way"; exit 1 }
if (-not $haveSongs) { Write-Host "VERIFY: incomplete - only --latencycheck ran"; exit 2 }
if ($Only.Count -gt 0) { Write-Host "VERIFY: the selected run(s) passed - $($runs.Count) of the full set, so this is not the whole bar"; exit 0 }
Write-Host "VERIFY: every run passed and stayed out of the way"; exit 0
