# Drives the real new app through its main flows on throwaway folders. See ..\SKILL.md.
#   smoke_app.ps1            build, run, clean up
#   smoke_app.ps1 -NoBuild   use the existing varManager.App\bin\Debug
# Run with Windows PowerShell 5.1. Everything lives under %TEMP%\varManager-verify-app, and nothing real is touched.
# The window can show white on this PC with hardware rendering, but UI Automation still drives it.
[CmdletBinding()]
param([switch]$NoBuild, [int]$TimeoutSeconds = 40)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$BinDir = Join-Path $RepoRoot 'varManager.App\bin\Debug\net10.0-windows'
$Base = Join-Path $env:TEMP 'varManager-verify-app'
$App = "$Base\app"; $Vam = "$Base\vam"; $Vars = "$Base\vars"
$Tidied = "$Vars\___VarTidied___"
$script:Passed = 0
$script:Failed = 0

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression, System.IO.Compression.FileSystem

function Check([bool]$Ok, [string]$Name) {
    if ($Ok) { $script:Passed++; Write-Host "PASS $Name" }
    else { $script:Failed++; Write-Host "FAIL $Name" -ForegroundColor Red }
}

function Remove-Sandbox {
    if (-not (Test-Path $Base)) { return }
    if (Test-Path "$Vam\AddonPackages") { cmd /c rmdir "$Vam\AddonPackages" 2>$null }
    cmd /c rmdir /s /q "$Base" 2>$null
}

function New-Var([string]$Name, [string]$References = '', [bool]$Scene = $false) {
    $file = [IO.File]::Open("$Vars\$Name.var", 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
    $meta = if ($Scene) { '{}' } else { "{`"x`":`"$References`"}" }
    $w = New-Object IO.StreamWriter($zip.CreateEntry('meta.json').Open()); $w.Write($meta); $w.Dispose()
    if ($Scene) {
        $w = New-Object IO.StreamWriter($zip.CreateEntry('Saves/scene/Day.json').Open()); $w.Write("{`"x`":`"$References`",`"g`":`"/Female/`"}"); $w.Dispose()
    }
    $zip.Dispose(); $file.Dispose()
}

$T = [Windows.Automation.TreeScope]
function All { $script:Win.FindAll($T::Descendants, [Windows.Automation.Condition]::TrueCondition) }
function Names { All | ForEach-Object { $_.Current.Name } | Where-Object { $_ } }
function Find([string]$Regex) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = All | Where-Object { $_.Current.Name -match $Regex } | Select-Object -First 1
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 200
    }
    throw "Nothing matched: $Regex"
}
function Press([string]$Regex) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($el in (All | Where-Object { $_.Current.Name -match $Regex -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.IsEnabled })) {
            $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw "No enabled button matched: $Regex"
}
function Page([string]$Name) {
    (Find "^$Name$").GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
}
function WaitNote([string]$Regex) { [void](Find $Regex) }
function Hashes { Get-ChildItem $Tidied -Recurse -Filter *.var | Sort-Object Name | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } }
function Links([string]$Profile) { @(Get-ChildItem "$Vam\___AddonPacksSwitch ___\$Profile\___VarsLink___" -Filter *.var -ErrorAction SilentlyContinue | ForEach-Object Name) }
function IsLink([string]$Path) { (Get-Item $Path -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) }

if (-not $NoBuild) {
    & dotnet build (Join-Path $RepoRoot 'varManager.App\varManager.App.csproj') -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
Remove-Sandbox
New-Item -ItemType Directory -Force $App, $Vam, $Vars | Out-Null
New-Item -ItemType File "$Vam\VaM.exe" | Out-Null
Copy-Item "$BinDir\*" $App -Recurse -Force
@{ VamPath = $Vam; VarsPath = $Vars } | ConvertTo-Json | Set-Content "$App\settings.json" -Encoding utf8

New-Var 'Good.Beach.1' 'Dep.Pack.1:/Custom/a.vap' $true
New-Var 'Dep.Pack.1'
New-Var 'Lone.Quiet.1' '' $true
New-Var 'Old.Thing.1'
New-Var 'Old.Thing.2'
New-Var 'Bad.Other.1' 'Gone.Pack.1:/Custom/a.vap'
New-Item -ItemType Directory -Force "$Vars\___VarRedundant___" | Out-Null
Set-Content "$Vars\___VarRedundant___\Dep.Pack.1.var" 'a spare copy'

$p = Start-Process "$App\varManager.App.exe" -WorkingDirectory $Base -PassThru
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 20 -and -not $p.MainWindowTitle) { Start-Sleep -Milliseconds 300; $p.Refresh() }
    Check ($p.MainWindowTitle -eq 'varManager') 'The window opens'
    $script:Win = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

    # The watch scans vars dropped in the repository by itself, a few seconds after the start.
    WaitNote 'Update DB finished: 6 new'
    Check ((Get-ChildItem $Tidied -Recurse -Filter *.var).Count -eq 6) 'Update DB tidies and scans the repository'
    Check (IsLink "$Vam\AddonPackages") 'AddonPackages stays a link'
    $before = Hashes
    Start-Sleep -Seconds 1

    # Turning on and off.
    Press '^Beach$'
    Press '^Turn on, with 1 dependency'
    WaitNote '^Turned on 2 vars'
    Check ((Links 'default').Count -eq 2) 'Turning on links the var and its dependency'
    Start-Sleep -Seconds 1
    # Turning off a dependency takes what needs it too.
    Press '^Pack$'
    Press '^Turn off'
    WaitNote '^Turned off 2 vars'
    Check ((Links 'default').Count -eq 0) 'Turning off a dependency takes what needs it'
    Start-Sleep -Seconds 1

    # Temporary links: a load links what it needs, and the next load clears what's left over.
    $temp = "$Vam\AddonPackages\___TempVarLink___"
    $request = "$Vam\Custom\PluginData\feelfar\loadscene.json"
    Press '^Beach$'
    Press '^Load in VaM$'
    WaitNote '^Asked VaM to load'
    Check ((Test-Path "$temp\Good.Beach.1.var") -and (Test-Path "$temp\Dep.Pack.1.var")) 'Load makes temporary links'
    Check (Test-Path $request) 'Load writes a request for the plugin'
    Remove-Item $request
    Start-Sleep -Seconds 1
    Press '^Quiet$'
    Press '^Load in VaM$'
    Start-Sleep -Seconds 2
    Remove-Item $request
    Start-Sleep -Seconds 24
    Check (-not (Test-Path "$temp\Good.Beach.1.var")) 'Old temporary links go after the next load'
    Check (Test-Path "$temp\Lone.Quiet.1.var") 'The new load keeps its own temporary link'

    # Rebuilding links: a var moved inside the tidied folder leaves a broken link for Health to repair.
    Press '^Beach$'
    Press '^Turn on, with 1 dependency'
    WaitNote '^Turned on 2 vars'
    New-Item -ItemType Directory -Force "$Tidied\Elsewhere" | Out-Null
    Move-Item "$Tidied\Dep\Dep.Pack.1.var" "$Tidied\Elsewhere\Dep.Pack.1.var"
    Page 'Health'
    Press '^Review Broken links'
    Press '^Repair links$'
    WaitNote '^Repaired 1 link'
    $link = "$Vam\AddonPackages\___VarsLink___\Dep.Pack.1.var"
    Check ((IsLink $link) -and ((Get-Content $link -Encoding Byte -TotalCount 2) -join ',') -eq '80,75') 'Repair points a broken link at the moved var'
    Start-Sleep -Seconds 2

    # Stale vars move with undo.
    Press '^Review Old versions'
    Press '^Move out$'
    WaitNote '^Moved 1 var'
    Check (-not (Test-Path "$Tidied\Old\Old.Thing.1.var")) 'A stale var moves out'
    Start-Sleep -Seconds 2
    Press '^Undo move$'
    WaitNote '^Move undone'
    Check (Test-Path "$Tidied\Old\Old.Thing.1.var") 'Undo brings the stale var back'
    Start-Sleep -Seconds 2

    # A reviewed delete.
    Press '^Review Duplicates'
    Press '^Delete for good$'
    WaitNote '^Deleted 1 file'
    Check (-not (Test-Path "$Vars\___VarRedundant___\Dep.Pack.1.var")) 'A reviewed duplicate is deleted'
    Start-Sleep -Seconds 1

    # A lean profile from a pick.
    Page 'Library'
    Press '^Quiet$'
    Press '^Add to profile picks$'
    WaitNote '^1 picked$'
    Press '^Build profile$'
    Press '^Save and switch$'
    WaitNote '^Switched to lean'
    Check ((Links 'lean').Count -eq 1) 'A lean profile links only what it needs'
    Check ([string]((Get-Item "$Vam\AddonPackages").Target) -match 'lean$') 'Saving switches to the new profile'
    Start-Sleep -Seconds 2
    Remove-Item "$Vam\___AddonPacksSwitch ___\lean\___VarsLink___\Lone.Quiet.1.var"
    Press '^Check$'
    Press '^Repair$'
    WaitNote '^Repairing lean finished'
    Check ((Links 'lean').Count -eq 1) 'Profile Check repairs a removed link'
    Start-Sleep -Seconds 2
    Press '^Switch to this profile$'
    WaitNote '^Switched to default'
    Check ((Links 'default').Count -eq 2) 'Switching back shows the default links'
    Start-Sleep -Seconds 1

    # Missing asks the Hub, which may not answer, so only the page is checked.
    Page 'Missing'
    WaitNote 'Nothing to download from the Hub|couldn''t be reached|ready on the Hub'
    Check $true 'Missing checks and answers'

    # A var dropped in later.
    New-Var 'Late.Arrival.1'
    WaitNote 'Update DB finished: 1 new'
    Check (Test-Path "$Tidied\Late\Late.Arrival.1.var") 'A dropped var is scanned by itself'

    $after = Hashes | Where-Object { $_ -notmatch '^Late\.' }
    Check (($before -join "`n") -eq ($after -join "`n")) 'No var file changed'
    Check (IsLink "$Vam\AddonPackages") 'AddonPackages is still a link'
}
catch {
    $script:Failed++
    Write-Host "FAIL $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000) }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Seconds 1
    Remove-Sandbox
}

if ($script:Failed -gt 0) {
    Write-Host "FAIL ($script:Failed failed, $script:Passed passed)" -ForegroundColor Red
    exit 1
}
Write-Host "PASS ($script:Passed checks passed)."
