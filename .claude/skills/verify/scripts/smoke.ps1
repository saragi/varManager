# varManager smoke test. See ..\SKILL.md for what it proves and how to read the evidence.
#   smoke.ps1             build, run the smoke test, clean up
#   smoke.ps1 -NoBuild    use the existing varManager\bin\Release
#   smoke.ps1 -Doctor     read-only check of this machine
#   smoke.ps1 -Cleanup    remove leftover sandboxes (never evidence)
# Run with Windows PowerShell 5.1. Everything the app touches lives under %TEMP%\varManager-verify\run\<stamp>.
[CmdletBinding()]
param([switch]$NoBuild, [switch]$Doctor, [switch]$Cleanup, [int]$TimeoutSeconds = 120)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$MSBuild = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$BinDir = Join-Path $RepoRoot 'varManager\bin\Release'
$VerifyRoot = Join-Path $env:TEMP 'varManager-verify'
$RunRoot = Join-Path $VerifyRoot 'run'
$EvidenceRoot = Join-Path $VerifyRoot 'evidence'
$PreviewTitle = 'Move library into varManager'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class Win
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    static extern IntPtr SendText(IntPtr h, uint m, IntPtr w, StringBuilder l, uint flags, uint ms, out IntPtr r);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendLen(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static IntPtr[] TopWindows(int pid)
    {
        List<IntPtr> found = new List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == (uint)pid && IsWindowVisible(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
    public static IntPtr[] Children(IntPtr parent)
    {
        List<IntPtr> found = new List<IntPtr>();
        EnumChildWindows(parent, delegate (IntPtr h, IntPtr l) { found.Add(h); return true; }, IntPtr.Zero);
        return found.ToArray();
    }
    public static string Text(IntPtr h)
    {
        IntPtr len;
        if (SendLen(h, 0x000E, IntPtr.Zero, IntPtr.Zero, 2, 2000, out len) == IntPtr.Zero) return "";
        StringBuilder s = new StringBuilder(len.ToInt32() + 1);
        IntPtr r;
        SendText(h, 0x000D, (IntPtr)s.Capacity, s, 2, 2000, out r);
        return s.ToString();
    }
    public static string ClassOf(IntPtr h)
    {
        StringBuilder s = new StringBuilder(256);
        GetClassName(h, s, s.Capacity);
        return s.ToString();
    }
    public static void Click(IntPtr h) { PostMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
    public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    public static void Shot(IntPtr h, string path)
    {
        RECT r;
        GetWindowRect(h, out r);
        using (Bitmap b = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T)))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                IntPtr dc = g.GetHdc();
                PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
            }
            b.Save(path, ImageFormat.Png);
        }
    }
}
'@

function Test-AceProvider {
    # A missing file proves the provider loaded. A missing provider fails with a different message.
    $probe = New-Object System.Data.OleDb.OleDbConnection "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$env:TEMP\varManager-verify-no-such.mdb"
    try { $probe.Open(); return $true }
    catch { return $_.Exception.Message -notmatch 'not registered' }
    finally { $probe.Dispose() }
}

function Remove-Sandbox([string]$Path) {
    if (-not $Path.StartsWith($RunRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to delete outside ${RunRoot}: $Path" }
    # rmdir removes directory symlinks without following them. Remove-Item in 5.1 can follow them.
    if (Test-Path -LiteralPath $Path) { cmd /c rmdir /s /q "`"$Path`"" }
}

if ($Doctor) {
    $bad = 0
    function Report([bool]$Ok, [string]$Text, [switch]$WarnOnly) {
        $tag = if ($Ok) { 'ok  ' } elseif ($WarnOnly) { 'warn' } else { 'FAIL' }
        if (-not $Ok -and -not $WarnOnly) { $script:bad++ }
        "$tag $Text"
    }
    Report ([Environment]::Is64BitProcess) '64-bit PowerShell (ACE provider is x64)'
    Report (Test-AceProvider) 'Microsoft.ACE.OLEDB.12.0 loads'
    Report (Test-Path $MSBuild) "MSBuild at $MSBuild"
    $exe = Join-Path $BinDir 'varManager.exe'
    if (Test-Path $exe) { Report $true "built exe: $((Get-Item $exe).LastWriteTime)" } else { Report $false 'no bin\Release\varManager.exe, run without -NoBuild' -WarnOnly }
    Report (-not (Get-Process -Name vam -ErrorAction SilentlyContinue)) 'VaM is not running (a running VaM blocks the migration step)'
    $others = @(Get-Process -Name varManager -ErrorAction SilentlyContinue)
    Report ($others.Count -eq 0) "other varManager processes: $($others.Count) (not ours, never kill them)" -WarnOnly
    $left = @(Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue)
    Report ($left.Count -eq 0) "leftover sandboxes: $($left.Count) (run -Cleanup)" -WarnOnly
    "evidence root: $EvidenceRoot"
    exit $bad
}

if ($Cleanup) {
    Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { Remove-Sandbox $_.FullName; "removed $($_.FullName)" }
    exit 0
}

$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Sandbox = Join-Path $RunRoot $Stamp
$Evidence = Join-Path $EvidenceRoot $Stamp
$App = Join-Path $Sandbox 'app'
$Vars = Join-Path $Sandbox 'vars'
$VaM = Join-Path $Sandbox 'vam'
$Pack = Join-Path $VaM 'AddonPackages'
New-Item -ItemType Directory -Force $Evidence | Out-Null
$Transcript = Join-Path $Evidence 'steps.log'
$Results = New-Object System.Collections.ArrayList
$script:Proc = $null

function Step([string]$Text) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $Text"
    Add-Content -LiteralPath $Transcript -Value $line
    Write-Host $line
}
function Check([bool]$Ok, [string]$Name) {
    [void]$Results.Add([pscustomobject]@{ ok = $Ok; check = $Name })
    Step "$(if ($Ok) { 'PASS' } else { 'FAIL' }) $Name"
    if (-not $Ok) { throw "Check failed: $Name" }
}
function New-FakeVar([string]$Path, [string]$Creator, [string]$Package) {
    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Open($Path, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
    $entries = @{
        'meta.json' = "{`"licenseType`":`"CC BY`",`"creatorName`":`"$Creator`",`"packageName`":`"$Package`",`"description`":`"verify seed`",`"dependencies`":{}}"
        'Saves/scene/smoke.json' = '{}'
    }
    foreach ($name in $entries.Keys) {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry($name).Open())
        $writer.Write($entries[$name])
        $writer.Dispose()
    }
    $zip.Dispose()
    $file.Dispose()
}
function Get-TreeListing([string]$Root) {
    # cmd dir does not follow directory symlinks, so a link cannot pull outside files into the listing.
    cmd /c dir /s /b /a "`"$Root`"" | Sort-Object
}
function Start-App {
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    $script:Proc = Start-Process -FilePath (Join-Path $App 'varManager.exe') -WorkingDirectory $App -PassThru
    Step "started varManager pid $($script:Proc.Id)"
}
function Wait-Window([string]$TitleRegex, [string]$Label, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ($script:Proc.HasExited) { throw "App exited (code $($script:Proc.ExitCode)) while waiting for $Label" }
        foreach ($h in [Win]::TopWindows($script:Proc.Id)) {
            if ([Win]::Text($h) -match $TitleRegex) { return $h }
        }
        Start-Sleep -Milliseconds 200
    }
    $seen = ([Win]::TopWindows($script:Proc.Id) | ForEach-Object { "[$([Win]::ClassOf($_))] $([Win]::Text($_))" }) -join '; '
    throw "Timed out waiting for $Label. Visible windows: $seen"
}
function Get-MessageBox {
    foreach ($h in [Win]::TopWindows($script:Proc.Id)) {
        if ([Win]::ClassOf($h) -eq '#32770') { return $h }
    }
    return [IntPtr]::Zero
}
function Wait-MessageBox([string]$Label, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ($script:Proc.HasExited) { throw "App exited while waiting for $Label" }
        $box = Get-MessageBox
        if ($box -ne [IntPtr]::Zero) { return $box }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $Label"
}
function Get-BoxText([IntPtr]$Box) {
    ([Win]::Children($Box) | Where-Object { [Win]::ClassOf($_) -eq 'Static' } | ForEach-Object { [Win]::Text($_) }) -join ' '
}
function Find-Child([IntPtr]$Parent, [string]$TextRegex) {
    foreach ($h in [Win]::Children($Parent)) {
        if ([Win]::Text($h) -match $TextRegex) { return $h }
    }
    throw "No child window matching $TextRegex"
}
function Save-Shot([IntPtr]$Window, [string]$Name) {
    Start-Sleep -Milliseconds 300
    [Win]::Shot($Window, (Join-Path $Evidence "$Name.png"))
}
function Read-Preview([IntPtr]$Dialog, [string]$Name) {
    $texts = [Win]::Children($Dialog) | ForEach-Object { [Win]::Text($_) }
    $body = $texts | Where-Object { $_ -match 'Nothing has been moved yet' } | Select-Object -First 1
    $intro = $texts | Where-Object { $_ -match '^AddonPackages is a real folder' } | Select-Object -First 1
    Set-Content -LiteralPath (Join-Path $Evidence "$Name.txt") -Value ($intro + "`r`n`r`n" + $body) -Encoding UTF8
    Save-Shot $Dialog $Name
    [pscustomobject]@{ Intro = [string]$intro; Body = [string]$body }
}
function Wait-Exit([int]$Seconds = 15) {
    if (-not $script:Proc.WaitForExit($Seconds * 1000)) { throw "App did not exit within $Seconds s" }
}
function Stop-App {
    if ($script:Proc -and -not $script:Proc.HasExited) {
        Stop-Process -Id $script:Proc.Id -Force -ErrorAction SilentlyContinue
        Step "killed pid $($script:Proc.Id) (started by this run)"
    }
}
function Get-VarRows {
    $db = New-Object System.Data.OleDb.OleDbConnection "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$App\varManager.mdb"
    $db.Open()
    try {
        $cmd = $db.CreateCommand()
        $cmd.CommandText = 'SELECT varName, varPath, creatorName, scenes FROM vars'
        $table = New-Object System.Data.DataTable
        $table.Load($cmd.ExecuteReader())
        return ,$table
    } finally { $db.Dispose() }
}

$failure = $null
try {
    if (-not (Test-AceProvider)) { throw 'Microsoft.ACE.OLEDB.12.0 x64 is missing. Stop and tell the user. Do not install it unasked.' }
    if (Get-Process -Name vam -ErrorAction SilentlyContinue) { throw 'VaM is running, which blocks the migration step. Stop and tell the user.' }

    if (-not $NoBuild) {
        Step 'building Release'
        & $MSBuild (Join-Path $RepoRoot 'varManager\varManager.csproj') /restore /t:Build /p:Configuration=Release /p:Platform=AnyCPU /v:m /nr:false *> (Join-Path $Evidence 'build.log')
        Check ($LASTEXITCODE -eq 0) 'MSBuild Release build succeeds'
    }

    Step "sandbox $Sandbox"
    New-Item -ItemType Directory -Force $App, $Vars, $Pack | Out-Null
    Copy-Item (Join-Path $BinDir '*') $App -Recurse -Exclude 'varManager.log', 'varsForInstall.txt'
    Set-Content -LiteralPath (Join-Path $VaM 'VaM.exe') -Value '' -NoNewline

    $configPath = Join-Path $App 'varManager.exe.config'
    [xml]$config = Get-Content -LiteralPath $configPath -Raw
    foreach ($pair in @(@('varspath', $Vars), @('vampath', $VaM))) {
        $node = $config.SelectSingleNode("//setting[@name='$($pair[0])']/value")
        if (-not $node) { throw "No $($pair[0]) setting in the config" }
        $node.InnerText = $pair[1]
    }
    $config.Save($configPath)
    [xml]$saved = Get-Content -LiteralPath $configPath -Raw
    Check (($saved.SelectSingleNode("//setting[@name='varspath']/value").InnerText -eq $Vars) -and
           ($saved.SelectSingleNode("//setting[@name='vampath']/value").InnerText -eq $VaM)) 'copied config points at the sandbox'
    Check ((Get-VarRows).Rows.Count -eq 0) 'copied database starts with no vars'

    New-FakeVar (Join-Path $Pack 'Smoke.Alpha.1.var') 'Smoke' 'Alpha'
    New-FakeVar (Join-Path $Pack 'Smoke.Beta.1.var.disabled') 'Smoke' 'Beta'
    Set-Content -LiteralPath (Join-Path $Pack 'readme.txt') -Value 'verify seed: not a var'
    $alphaHash = (Get-FileHash (Join-Path $Pack 'Smoke.Alpha.1.var')).Hash
    $before = Get-TreeListing $VaM

    # Pass 1: the preview must be truthful and Cancel must leave everything alone.
    Start-App
    $dialog = Wait-Window ([regex]::Escape($PreviewTitle)) 'migration preview'
    $preview = Read-Preview $dialog 'preview-cancel'
    Check ($preview.Intro -match [regex]::Escape("From: $Pack")) 'preview names the sandbox AddonPackages'
    Check ($preview.Intro -match [regex]::Escape("To: $Vars")) 'preview names the sandbox vars folder'
    Check ($preview.Body -match 'Vars to move and link back: 1 ') 'preview counts 1 var'
    Check ($preview.Body -match 'Disabled vars to store unlinked: 1 ') 'preview counts 1 disabled var'
    Check ($preview.Body -match 'Other files staying with AddonPackages: 1') 'preview counts 1 other file'
    [Win]::Click((Find-Child $dialog '^Cancel$'))
    Wait-Exit
    Check ((Compare-Object $before (Get-TreeListing $VaM) | Measure-Object).Count -eq 0) 'Cancel moves nothing'
    Check (-not (Get-Item -LiteralPath $Pack).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) 'Cancel leaves AddonPackages a real folder'
    Check (@(Get-ChildItem -LiteralPath $Vars -Recurse -Force).Count -eq 0) 'Cancel leaves the vars folder empty'

    # Pass 2: confirm, on seed data only. The guard reads the paths from the live dialog, because a
    # user.config beside the settings store could override the copied config.
    Start-App
    $dialog = Wait-Window ([regex]::Escape($PreviewTitle)) 'migration preview'
    $preview = Read-Preview $dialog 'preview-confirm'
    if (($preview.Intro -notmatch [regex]::Escape("From: $Pack")) -or ($preview.Intro -notmatch [regex]::Escape("To: $Vars"))) {
        throw "SAFETY: preview paths are not the sandbox, refusing to click Move files: $($preview.Intro)"
    }
    $move = Find-Child $dialog '^Move files$'
    Check ([Win]::IsWindowEnabled($move)) 'Move files is enabled (same drive)'
    [Win]::Click($move)
    $box = Wait-MessageBox 'migration result'
    $boxText = Get-BoxText $box
    Save-Shot $box 'migration-result'
    Check ($boxText -match '^Files moved') 'migration reports Files moved'
    [Win]::Click((Find-Child $box '^OK$'))

    $main = Wait-Window '^VarManager\s+v' 'main window'
    Check ((Get-Item -LiteralPath $Pack).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) 'AddonPackages is now a symlink'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $Vars '___MigrationLogs___') -Filter '*.tsv').Count -eq 1) 'migration log written'
    Check ((Get-Content -LiteralPath (Join-Path $App 'varsForInstall.txt')) -contains 'Smoke.Alpha.1') 'enabled var queued in varsForInstall.txt'

    # Update DB. The startup job can still hold the worker, so retry on the "still running" box.
    $tidied = Join-Path $Vars '___VarTidied___\Smoke\Smoke.Alpha.1.var'
    $link = Join-Path $Pack '___VarsLink___\Smoke.Alpha.1.var'
    $started = $false
    for ($try = 0; $try -lt 5 -and -not $started; $try++) {
        [Win]::Click((Find-Child $main '^UPD_DB$'))
        $confirm = Wait-Window '^UpdateDB$' 'Update DB confirmation' 10
        if ($try -eq 0) { Save-Shot $confirm 'updatedb-confirm' }
        [Win]::Click((Find-Child $confirm '^&?Yes$'))
        Start-Sleep -Seconds 1
        $box = Get-MessageBox
        if ($box -ne [IntPtr]::Zero -and (Get-BoxText $box) -match 'still running') {
            [Win]::Click((Find-Child $box '^OK$'))
            Start-Sleep -Seconds 2
        } else { $started = $true }
    }
    Check $started 'Update DB job started'

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $done = $false
    while ((Get-Date) -lt $deadline -and -not $done) {
        $box = Get-MessageBox
        if ($box -ne [IntPtr]::Zero) {
            Save-Shot $box 'unexpected-dialog'
            throw "Unexpected dialog during Update DB: $(Get-BoxText $box)"
        }
        $done = (Test-Path -LiteralPath $link) -and -not (Test-Path -LiteralPath (Join-Path $App 'varsForInstall.txt'))
        if (-not $done) { Start-Sleep -Milliseconds 500 }
    }
    Check $done 'Update DB finished: link created and varsForInstall.txt consumed'
    Start-Sleep -Seconds 2
    Save-Shot $main 'main-after-updatedb'

    [Win]::Close($main)
    Wait-Exit

    Check (Test-Path -LiteralPath $tidied) 'var tidied into ___VarTidied___\Smoke'
    Check ((Get-FileHash -LiteralPath $tidied).Hash -eq $alphaHash) 'var contents unchanged'
    Check ((Get-Item -LiteralPath $link).LinkType -eq 'SymbolicLink') 'var linked into ___VarsLink___'
    Check (([string](Get-Item -LiteralPath $link).Target) -eq $tidied) 'link targets the tidied var'
    Check (-not (Test-Path -LiteralPath (Join-Path $Pack '___VarsLink___\Smoke.Beta.1.var'))) 'disabled var stays unlinked'
    Check (Test-Path -LiteralPath (Join-Path $Pack 'readme.txt')) 'other file stays with AddonPackages'
    $rows = Get-VarRows
    $rows | Select-Object varName, varPath, creatorName, scenes | Format-Table -AutoSize | Out-File -LiteralPath (Join-Path $Evidence 'vars-table.txt') -Encoding utf8
    Check (@($rows.Select("varName = 'Smoke.Alpha.1' AND creatorName = 'Smoke' AND scenes = 1")).Count -eq 1) 'database row for Smoke.Alpha.1 with 1 scene'
    Check (@($rows.Select("varName = 'Smoke.Beta.1'")).Count -eq 1) 'database row for the disabled var'
    $logFile = Join-Path $App 'varManager.log'
    Check (-not (Select-String -LiteralPath $logFile -Pattern 'ERROR|FATAL' -Quiet)) 'app log has no errors'
}
catch {
    $failure = $_.Exception.Message
    Step "FAILED: $failure"
    if ($script:Proc -and -not $script:Proc.HasExited) {
        foreach ($h in [Win]::TopWindows($script:Proc.Id)) { try { Save-Shot $h "failure-$($h.ToInt64())" } catch { } }
    }
}
finally {
    Stop-App
    if (Test-Path -LiteralPath $Sandbox) {
        Get-TreeListing $Sandbox | Where-Object { $_ -notlike "$App\*" } | Set-Content -LiteralPath (Join-Path $Evidence 'tree.txt') -Encoding UTF8
        foreach ($name in 'varManager.log', 'varManager.exe.config') {
            $source = Join-Path $App $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source $Evidence }
        }
        Get-ChildItem -LiteralPath (Join-Path $Vars '___MigrationLogs___') -ErrorAction SilentlyContinue | Copy-Item -Destination $Evidence
        Remove-Sandbox $Sandbox
    }
    $passed = @($Results | Where-Object ok).Count
    $verdict = if ($failure) { 'FAIL' } else { 'PASS' }
    [pscustomobject]@{ verdict = $verdict; passed = $passed; failure = $failure; commit = (git -C $RepoRoot rev-parse --short HEAD); checks = $Results } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Evidence 'result.json') -Encoding UTF8
    Write-Host "$verdict ($passed checks passed). Evidence: $Evidence"
}
if ($failure) { exit 1 }
exit 0
