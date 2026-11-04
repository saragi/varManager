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
function New-FakeVar([string]$Path, [string]$Creator, [string]$Package, [string]$ExtraReference = 'Smoke.Alpha.1', [string]$HairReference = 'Deep Creator.Hidden_Pack.min2', [switch]$LongEntry) {
    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Open($Path, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
    $entries = @{
        'meta.json' = "{`"licenseType`":`"CC BY`",`"creatorName`":`"$Creator`",`"packageName`":`"$Package`",`"description`":`"verify seed`",`"dependencies`":{}}"
        'Saves/scene/smoke.json' = '{"hair":"' + $HairReference + ':/Custom/x.vap","own":"' + $ExtraReference + ':/Custom/y.vap","local":"SELF:/Custom/z.vap"}'
    }
    # The database holds 255 characters, so this entry can't get a row, but the var must still be recorded.
    if ($LongEntry) { $entries['Saves/scene/' + ('x' * 260) + '.json'] = '{}' }
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
function Get-VarRows { Get-TableRows 'SELECT varName, varPath, creatorName, scenes FROM vars' }
function Get-DependencyRows { Get-TableRows 'SELECT varName, dependency FROM dependencies' }
function Get-TableRows([string]$Query) {
    $db = New-Object System.Data.OleDb.OleDbConnection "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$App\varManager.mdb"
    $db.Open()
    try {
        $cmd = $db.CreateCommand()
        $cmd.CommandText = $Query
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
    Copy-Item (Join-Path $BinDir '*') $App -Recurse -Exclude 'varManager.log', 'varsForInstall.txt', 'varsMigrated.txt'
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

    # Alpha depends on disabled Beta, so the unlinked check below proves dependencies aren't followed.
    New-FakeVar (Join-Path $Pack 'Smoke.Alpha.1.var') 'Smoke' 'Alpha' 'Smoke.Alpha.1' 'Smoke.Beta.1'
    New-FakeVar (Join-Path $Pack 'Smoke.Beta.1.var.disabled') 'Smoke' 'Beta' 'Smoke.Extra.1'
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
    Check ((Get-Content -LiteralPath (Join-Path $App 'varsMigrated.txt')) -contains 'Smoke.Alpha.1') 'enabled var queued in varsMigrated.txt'

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
        $done = (Test-Path -LiteralPath $link) -and -not (Test-Path -LiteralPath (Join-Path $App 'varsMigrated.txt'))
        if (-not $done) { Start-Sleep -Milliseconds 500 }
    }
    Check $done 'Update DB finished: link created and varsMigrated.txt consumed'
    Start-Sleep -Seconds 2
    Save-Shot $main 'main-after-updatedb'
    Check ((Get-FileHash -LiteralPath $tidied).Hash -eq $alphaHash) 'var contents unchanged by first Update DB'

    $firstDependencies = Get-DependencyRows
    Check (@($firstDependencies.Select("varName = 'Smoke.Beta.1' AND dependency = 'Deep Creator.Hidden_Pack.min2'")).Count -eq 1) 'deep scan finds a scene reference absent from meta.json'
    Check (@($firstDependencies.Select("varName = 'Smoke.Alpha.1' AND dependency = 'Smoke.Alpha.1'")).Count -eq 0) 'a var is not its own dependency'
    Check (@($firstDependencies.Select("varName = 'Smoke.Alpha.1' AND dependency = 'Smoke.Beta.1'")).Count -eq 1) 'enabled var depends on the disabled var'
    Check (-not (Test-Path -LiteralPath (Join-Path $Pack '___VarsLink___\Smoke.Beta.1.var'))) 'migration leaves a needed disabled var unlinked'

    # A var replaced by a different file must be read again, not skipped by name.
    $oldLength = (Get-Item -LiteralPath $tidied).Length
    [IO.File]::Delete($tidied)
    New-FakeVar $tidied 'Smoke' 'Alpha' 'Replaced.Package_With_A_Longer_Name.latest' 'Replaced.Hair_Package.latest' -LongEntry
    Check ((Get-Item -LiteralPath $tidied).Length -ne $oldLength) 'replacement var has a different size'
    $alphaHash = (Get-FileHash -LiteralPath $tidied).Hash
    # A var that can't be linked must stay listed, so a later Update DB can retry it.
    $migratedList = Join-Path $App 'varsMigrated.txt'
    Set-Content -LiteralPath $migratedList -Value 'Ghost.Missing.1', 'Smoke.Alpha.1'
    $started = $false
    for ($try = 0; $try -lt 5 -and -not $started; $try++) {
        [Win]::Click((Find-Child $main '^UPD_DB$'))
        $confirm = Wait-Window '^UpdateDB$' 'Update DB confirmation' 10
        [Win]::Click((Find-Child $confirm '^&?Yes$'))
        Start-Sleep -Seconds 1
        $box = Get-MessageBox
        if ($box -ne [IntPtr]::Zero -and (Get-BoxText $box) -match 'still running') {
            [Win]::Click((Find-Child $box '^OK$'))
            Start-Sleep -Seconds 2
        } else { $started = $true }
    }
    Check $started 'second Update DB job started'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $rescanned = $false
    while ((Get-Date) -lt $deadline -and -not $rescanned) {
        Start-Sleep -Milliseconds 700
        $current = Get-DependencyRows
        $rescanned = (@($current.Select("varName = 'Smoke.Alpha.1' AND dependency = 'Replaced.Package_With_A_Longer_Name.latest'")).Count -eq 1) -and
            (@($current.Select("varName = 'Smoke.Alpha.1' AND dependency = 'Replaced.Hair_Package.latest'")).Count -eq 1)
    }
    Check $rescanned 'Update DB rescans a var whose file changed'
    Start-Sleep -Seconds 1
    Check ((@(Get-Content -LiteralPath $migratedList) -join ',') -eq 'Ghost.Missing.1') 'unlinked migrated var stays listed for retry'
    Check (@($current.Select("varName = 'Smoke.Alpha.1' AND dependency = 'Smoke.Beta.1'")).Count -eq 0) 'rescan clears the old dependency rows'
    # The app gives no completion signal, so allow its last steps to finish.
    Start-Sleep -Seconds 2

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
    Check (@($rows.Select("varName = 'Smoke.Alpha.1' AND creatorName = 'Smoke' AND scenes = 2")).Count -eq 1) 'a var with an overlong entry is still recorded, with both scenes counted'
    Check (@($rows.Select("varName = 'Smoke.Beta.1'")).Count -eq 1) 'database row for the disabled var'
    $dependencies = Get-DependencyRows
    $dependencies | Select-Object varName, dependency | Format-Table -AutoSize | Out-File -LiteralPath (Join-Path $Evidence 'dependencies-table.txt') -Encoding utf8
    Check (@($dependencies.Select("varName = 'Smoke.Beta.1' AND dependency = 'Smoke.Extra.1'")).Count -eq 1) 'deep scan finds a reference to another var'
    $logFile = Join-Path $App 'varManager.log'
    Check (-not (Select-String -LiteralPath $logFile -Pattern 'ERROR|FATAL' -Quiet)) 'app log has no errors'

    # Lean profile: runs last because it repoints AddonPackages away from the default switch.
    Start-App
    $main = Wait-Window '^VarManager' 'main window' 30
    Start-Sleep -Seconds 3
    # Select the first row by clicking its row header, as a user would.
    # UI Automation exposes no IDs here, so walk down to the control under a point in the list.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Smoke {
    public static class Mouse {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        public static IntPtr ControlAt(IntPtr window, int x, int y) {
            POINT screen = new POINT { X = x, Y = y };
            ClientToScreen(window, ref screen);
            IntPtr current = window;
            while (true) {
                POINT client = screen;
                ScreenToClient(current, ref client);
                IntPtr child = ChildWindowFromPointEx(current, client, 1);
                if (child == IntPtr.Zero || child == current) return current;
                current = child;
            }
        }
    }
}
'@
    $gridHandle = [Smoke.Mouse]::ControlAt($main, 420, 300)
    Check ($gridHandle -ne $main) 'var list found'
    $point = [IntPtr]((36 -shl 16) -bor 12)
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0201, [IntPtr]1, $point)
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0202, [IntPtr]::Zero, $point)
    Start-Sleep -Seconds 1
    [Win]::Click((Find-Child $main '^Lean Profile$'))
    $leanForm = Wait-Window '^Lean profile$' 'lean profile window' 15
    Save-Shot $leanForm 'lean-profile'
    $leanText = ([Win]::Children($leanForm) | ForEach-Object { [Win]::Text($_) }) -join "`n"
    Check ($leanText -match 'Vars in this profile: \d') 'lean profile window shows a preview'
    [Win]::Click((Find-Child $leanForm '^Create profile$'))
    $leanLinks = Join-Path $VaM '___AddonPacksSwitch ___\lean\___VarsLink___'
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath (Join-Path $Vars '___Profiles___\lean.profile'))) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Seconds 3
    Check (Test-Path -LiteralPath (Join-Path $Vars '___Profiles___\lean.profile')) 'lean profile saved'
    $leanVars = @(Get-ChildItem -LiteralPath $leanLinks -Filter '*.var' -ErrorAction SilentlyContinue)
    Check (($leanVars.Name) -contains 'Smoke.Alpha.1.var') 'lean profile links the selected var'
    $profileFile = Join-Path $Vars '___Profiles___\lean.profile'
    Check ((Get-Content -LiteralPath $profileFile) -contains "direct`tSmoke.Alpha.1") 'lean profile records the direct pick'
    Check (@($leanVars | Where-Object { $_.LinkType -ne 'SymbolicLink' }).Count -eq 0) 'lean profile holds only links'
    Check (([string](Get-Item -LiteralPath $Pack).Target) -match 'lean$') 'AddonPackages points at the lean profile'
    Check ((Get-FileHash -LiteralPath $tidied).Hash -eq $alphaHash) 'creating a profile leaves var files unchanged'
    [Win]::Click((Find-Child $main '^Profile Check$'))
    $checkForm = Wait-Window '^Profile check$' 'profile check window' 15
    Save-Shot $checkForm 'profile-check'
    $checkText = ([Win]::Children($checkForm) | ForEach-Object { [Win]::Text($_) }) -join "`n"
    Check ($checkText -match 'Replaced\.Hair_Package\.latest') 'profile check lists missing packages'
    [Win]::Click((Find-Child $checkForm '^Close$'))
    Start-Sleep -Seconds 1

    # Repair puts back a lost link through the path that refreshes the active switch.
    $alphaLeanLink = Join-Path $leanLinks 'Smoke.Alpha.1.var'
    [IO.File]::Delete($alphaLeanLink)
    [Win]::Click((Find-Child $main '^Profile Check$'))
    $checkForm = Wait-Window '^Profile check$' 'profile check window' 15
    [Win]::Click((Find-Child $checkForm '^Repair$'))
    Start-Sleep -Seconds 3
    Check (Test-Path -LiteralPath $alphaLeanLink) 'Repair restores a deleted link'

    # A second version with Beta selected, then Restore previous brings the first back.
    $rowTwo = [IntPtr]((63 -shl 16) -bor 12)
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0201, [IntPtr]1, $rowTwo)
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0202, [IntPtr]::Zero, $rowTwo)
    Start-Sleep -Seconds 1
    [Win]::Click((Find-Child $main '^Lean Profile$'))
    $leanForm = Wait-Window '^Lean profile$' 'lean profile window' 15
    [Win]::Click((Find-Child $leanForm '^Create profile$'))
    Start-Sleep -Seconds 3
    $betaLeanLink = Join-Path $leanLinks 'Smoke.Beta.1.var'
    Check (Test-Path -LiteralPath $betaLeanLink) 'second profile version links the new selection'
    [Win]::Click((Find-Child $main '^Profile Check$'))
    $checkForm = Wait-Window '^Profile check$' 'profile check window' 15
    [Win]::Click((Find-Child $checkForm '^Restore previous$'))
    Start-Sleep -Seconds 3
    Check ((Get-Content -LiteralPath $profileFile) -contains "direct`tSmoke.Alpha.1") 'Restore previous brings back the saved picks'
    Check (-not (Test-Path -LiteralPath $betaLeanLink)) 'Restore previous removes links it no longer needs'
    Check (Test-Path -LiteralPath $alphaLeanLink) 'Restore previous keeps links it needs'

    # Deleting the switch removes its links and profile files, and returns to default.
    [Win]::Click((Find-Child $main '^Del$'))
    $deleteBox = Wait-Window '^delete switch$' 'delete switch confirmation' 10
    [Win]::Click((Find-Child $deleteBox '^&?Yes$'))
    Start-Sleep -Seconds 3
    Check (-not (Test-Path -LiteralPath $profileFile)) 'Del removes the profile file'
    Check (-not (Test-Path -LiteralPath "$profileFile.previous")) 'Del removes the prior profile version'
    Check (-not (Test-Path -LiteralPath (Split-Path $leanLinks))) 'Del removes the switch folder'
    Check (Test-Path -LiteralPath $tidied) 'Del leaves var files in place'
    Check (([string](Get-Item -LiteralPath $Pack).Target) -match 'default$') 'AddonPackages returns to the default switch'

    # An exclude profile is default minus the selected var, and Profile Check treats it as a profile.
    # Alpha no longer needs Beta here, so with Beta linked in default, leaving Beta out must keep Alpha.
    # A broken link must not block it.
    $defaultLinks = Join-Path $VaM '___AddonPacksSwitch ___\default\___VarsLink___'
    $betaFile = (Get-ChildItem -LiteralPath (Split-Path (Split-Path $tidied)) -Recurse -Filter 'Smoke.Beta.1.var').FullName
    $defaultBeta = Join-Path $defaultLinks 'Smoke.Beta.1.var'
    $defaultGhost = Join-Path $defaultLinks 'Ghost.Gone.1.var'
    cmd /c mklink "$defaultBeta" "$betaFile" | Out-Null
    cmd /c mklink "$defaultGhost" (Join-Path $Vars 'no-such-file.var') | Out-Null
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0201, [IntPtr]1, $rowTwo)
    [void][Smoke.Mouse]::PostMessage($gridHandle, 0x0202, [IntPtr]::Zero, $rowTwo)
    Start-Sleep -Seconds 1
    [Win]::Click((Find-Child $main '^Exclude Profile$'))
    $excludeForm = Wait-Window '^Exclude profile$' 'exclude profile window' 15
    Save-Shot $excludeForm 'exclude-profile'
    $excludeText = ([Win]::Children($excludeForm) | ForEach-Object { [Win]::Text($_) }) -join "`n"
    Check ($excludeText -match 'Left out:') 'exclude profile window shows what is left out'
    [Win]::Click((Find-Child $excludeForm '^Create profile$'))
    $trimFile = Join-Path $Vars '___Profiles___\trimmed.profile'
    $trimLinks = Join-Path $VaM '___AddonPacksSwitch ___\trimmed\___VarsLink___'
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $trimFile)) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Seconds 3
    Check ((Get-Content -LiteralPath $trimFile) -contains "exclude`tSmoke.Beta.1") 'exclude profile records the exclusion'
    Check (-not (Test-Path -LiteralPath (Join-Path $trimLinks 'Smoke.Beta.1.var'))) 'exclude profile leaves the var unlinked'
    $trimAlpha = Join-Path $trimLinks 'Smoke.Alpha.1.var'
    Check (Test-Path -LiteralPath $trimAlpha) 'exclude profile links the rest of default'
    Check (-not (Test-Path -LiteralPath (Join-Path $trimLinks 'Ghost.Gone.1.var'))) 'exclude profile skips a broken default link'
    Check (Test-Path -LiteralPath $defaultBeta) 'exclude profile leaves default alone'
    Check (([string](Get-Item -LiteralPath $Pack).Target) -match 'trimmed$') 'AddonPackages points at the exclude profile'
    Check ((Get-FileHash -LiteralPath $tidied).Hash -eq $alphaHash) 'exclude profile leaves var files unchanged'
    [Win]::Click((Find-Child $main '^Profile Check$'))
    $checkForm = Wait-Window '^Profile check$' 'exclude profile check window' 15
    [Win]::Click((Find-Child $checkForm '^Close$'))
    Start-Sleep -Seconds 1
    [IO.File]::Delete($trimAlpha)
    [Win]::Click((Find-Child $main '^Profile Check$'))
    $checkForm = Wait-Window '^Profile check$' 'exclude profile repair window' 15
    [Win]::Click((Find-Child $checkForm '^Repair$'))
    Start-Sleep -Seconds 3
    Check (Test-Path -LiteralPath $trimAlpha) 'Repair restores a link in an exclude profile'
    [IO.File]::Delete($defaultBeta)
    [IO.File]::Delete($defaultGhost)
    [Win]::Click((Find-Child $main '^Del$'))
    $deleteBox = Wait-Window '^delete switch$' 'delete exclude switch confirmation' 10
    [Win]::Click((Find-Child $deleteBox '^&?Yes$'))
    Start-Sleep -Seconds 3
    Check (([string](Get-Item -LiteralPath $Pack).Target) -match 'default$') 'AddonPackages returns to default after the exclude profile'

    # The Missing window opens by itself into a checked list, with or without a network.
    [Win]::Click((Find-Child $main '^Missing( \d+)?$'))
    $missingForm = Wait-Window '^Missing$' 'missing window' 15
    Start-Sleep -Seconds 12
    Save-Shot $missingForm 'missing'
    $missingText = ([Win]::Children($missingForm) | ForEach-Object { [Win]::Text($_) }) -join "`n"
    Check ($missingText -match 'Checked\.|couldn.t be reached') 'missing window finishes its check'
    [Win]::Close($missingForm)
    Start-Sleep -Seconds 1
    [Win]::Close($main)
    Wait-Exit
    Check (-not (Select-String -LiteralPath $logFile -Pattern 'ERROR|FATAL' -Quiet)) 'app log has no errors after profile work'

    # Full rollback returns the library to a real folder.
    $moveLog = (Get-ChildItem -LiteralPath (Join-Path $Vars '___MigrationLogs___') -Filter '*.tsv' | Select-Object -First 1).FullName
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    $script:Proc = Start-Process -FilePath (Join-Path $App 'varManager.exe') -ArgumentList '--rollback-migration', "`"$moveLog`"" -WorkingDirectory $App -PassThru
    $resultBox = Wait-MessageBox 'rollback result' 20
    $resultText = Get-BoxText $resultBox
    [Win]::Click((Find-Child $resultBox '^OK$'))
    Wait-Exit
    Check ($resultText -match 'Failed: 0') 'rollback reports no failures'
    Check (-not (Get-Item -LiteralPath $Pack).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) 'rollback leaves AddonPackages a real folder'
    Check ((Get-FileHash -LiteralPath (Join-Path $Pack 'Smoke.Alpha.1.var')).Hash -eq $alphaHash) 'rollback returns the var unchanged'
    Check (-not (Test-Path -LiteralPath $tidied)) 'rollback moves the var out of the repository'
    Check (-not (Test-Path -LiteralPath (Join-Path $VaM '___AddonPacksSwitch ___\default'))) 'rollback leaves no default switch'
    Check (Test-Path -LiteralPath (Join-Path $Pack 'Smoke.Beta.1.var.disabled')) 'rollback returns the disabled var'
    Check (Test-Path -LiteralPath (Join-Path $Pack 'readme.txt')) 'rollback returns other files'
    Check (-not (Test-Path -LiteralPath (Join-Path $Pack '___VarsLink___'))) 'rollback removes var links'
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
