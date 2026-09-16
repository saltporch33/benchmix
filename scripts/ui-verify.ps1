<#
    Drives the real Benchmix UI through UI Automation and checks what it did.

    This exists because "it compiles and the unit tests pass" says nothing about whether clicking
    a button in the actual window does the right thing. UIA clicks the real controls in the real
    app, then reads the real labels back.

    It runs the window minimised and never steals focus, so it is safe to run while someone is
    using the machine.

    Usage:  powershell -ExecutionPolicy Bypass -File scripts\ui-verify.ps1
    Exit:   0 = all checks passed, 1 = something failed
#>

$ErrorActionPreference = 'Stop'

$exe = "$env:LOCALAPPDATA\Benchmix.build\publish\Benchmix.App\release_win-x64\Benchmix.exe"
if (-not (Test-Path $exe)) { Write-Host "publish the app first: dotnet publish src\Benchmix.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true"; exit 1 }

$audio = "$env:USERPROFILE\Music\Sports DJ\audio"
$bed = Join-Path $audio "CFBCA39E-E325-485F-839D-CF95466767AA.mp3"   # Still D.R.E.  93 BPM G#m
$top = Join-Path $audio "9523F493-5F99-4F39-9784-0C129F7286A7.mp3"   # T.R.O.Y.     102 BPM G#m
if (-not (Test-Path $bed) -or -not (Test-Path $top)) { Write-Host "SKIP: sample library not present"; exit 0 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$AE = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants

$failures = 0
function Check($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" }
    else { Write-Host "  FAIL  $name  -- $detail"; $script:failures++ }
}

$p = Start-Process -FilePath $exe -ArgumentList @("`"$bed`"", "`"$top`"", "--autoclose", "120") -PassThru -WindowStyle Minimized
try {
    Start-Sleep -Seconds 14
    $p.Refresh()
    if ($p.MainWindowHandle -eq 0) { Write-Host "FAIL: no window appeared"; exit 1 }

    function Labels {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        $AE::FromHandle($p.MainWindowHandle).FindAll($scope, $c) | ForEach-Object { $_.Current.Name }
    }

    function Buttons($label) {
        $root = $AE::FromHandle($p.MainWindowHandle)
        $n = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $label)
        $t = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $root.FindAll($scope, (New-Object System.Windows.Automation.AndCondition($n, $t)))
    }

    function Click($element) {
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1500
    }

    Write-Host "`nLoading"
    $before = Labels
    Check "both tracks loaded"      (($before -join '|') -match 'Added 2 tracks')                  "status did not report two tracks"
    Check "bed tempo from tag"      (($before -join '|') -match '93\.0 BPM')                       "93.0 BPM not shown"
    Check "topper tempo from tag"   (($before -join '|') -match '102\.0 BPM')                      "102.0 BPM not shown"
    Check "keys read from tags"     (($before -join '|') -match 'G#m')                             "G#m not shown"

    Write-Host "`nMatch BPM on the second track"
    $match = Buttons "Match BPM"
    Check "two Match BPM buttons"   ($match.Count -eq 2)                                           "found $($match.Count)"
    if ($match.Count -eq 2) {
        Click $match.Item(1)
        $after = Labels -join '|'
        Check "topper stretched to 93"  ($after -match '93\.0 BPM\s+\(was 102\.0\)')               "label did not update"
        Check "status explains it"      ($after -match '-8\.8% to hit 93\.0 BPM')                  "status did not report the stretch"
    }

    Write-Host "`nReset on the second track"
    $reset = Buttons "Reset"
    if ($reset.Count -ge 2) {
        Click $reset.Item(1)
        $after = Labels -join '|'
        Check "topper back to 102"      ($after -match '102\.0 BPM')                               "reset did not restore the tempo"
        Check "status confirms reset"   ($after -match 'original tempo, pitch and EQ')             "status did not confirm"
    } else { Check "Reset buttons present" $false "found $($reset.Count)" }

    Write-Host "`nMatch key"
    $key = Buttons "Match key"
    if ($key.Count -ge 2) {
        Click $key.Item(1)
        $after = Labels -join '|'
        # Both tracks are G#m, so the correct answer is no shift at all.
        Check "same key recognised"     ($after -match 'same key')                                 "did not report the keys as matching"
    } else { Check "Match key buttons present" $false "found $($key.Count)" }
}
finally {
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
}

Write-Host ""
if ($failures -eq 0) { Write-Host "UI VERIFY: all checks passed"; exit 0 }
Write-Host "UI VERIFY: $failures check(s) failed"; exit 1
