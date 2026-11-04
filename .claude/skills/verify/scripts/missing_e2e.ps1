# End to end on throwaway folders: drop a var, let the app scan it, then Get all from the real Hub.
# Build Release first. Exits non-zero when a check fails.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path 'D:\varManager\varManager\bin\Release\varManager.exe')) { throw 'Build Release first' }
function Need([bool]$ok, [string]$name) { "$(if ($ok) { 'PASS' } else { 'FAIL' }) $name"; if (-not $ok) { throw "Check failed: $name" } }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.IO.Compression
Add-Type -Namespace W -Name U5 -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
$base = Join-Path $env:TEMP 'varManager-missing-e2e'
if (Test-Path $base) { cmd /c rmdir /s /q "$base" }
$app = "$base\app"; $vars = "$base\vars"; $vam = "$base\vam"
New-Item -ItemType Directory -Force $app, $vars, "$vam\___AddonPacksSwitch ___\default\___VarsLink___" | Out-Null
New-Item -ItemType File "$vam\VaM.exe" | Out-Null
cmd /c mklink /d "$vam\AddonPackages" "$vam\___AddonPacksSwitch ___\default" | Out-Null
Copy-Item 'D:\varManager\varManager\bin\Release\*' $app -Recurse -Force
Remove-Item "$app\varManager.log" -ErrorAction SilentlyContinue
$cfg = "$app\varManager.exe.config"; [xml]$x = Get-Content $cfg
foreach ($st in $x.configuration.userSettings.'varManager.Properties.Settings'.setting) {
  if ($st.name -eq 'varspath') { $st.value = $vars }
  if ($st.name -eq 'vampath') { $st.value = $vam }
}
$x.Save($cfg)
function New-Var([string]$path) {
  $file = [IO.File]::Open($path, 'CreateNew')
  $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
  $e = @{ 'meta.json' = '{"licenseType":"CC BY","creatorName":"E2E","packageName":"Scene","description":"x","dependencies":{}}'
          'Saves/scene/e2e.json' = '{"a":"Kamiyama Prod.Preset_Dora_26y_V2.latest:/Custom/x.vap","b":"Nobody.Gone_Pack.1:/Custom/y.vap"}' }
  foreach ($n in $e.Keys) { $w = New-Object IO.StreamWriter($zip.CreateEntry($n).Open()); $w.Write($e[$n]); $w.Dispose() }
  $zip.Dispose(); $file.Dispose()
}
$root = [Windows.Automation.AutomationElement]::RootElement
function Wins($p) { $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id); $root.FindAll([Windows.Automation.TreeScope]::Children, $c) }
function All($p) { foreach ($w in (Wins $p)) { $w; foreach ($e in $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) { $e } } }
function Wait-El($p, $rx, $t) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $t) { $h = All $p | Where-Object { $_.Current.Name -match $rx } | Select-Object -First 1; if ($h) { return $h }; Start-Sleep -Milliseconds 500 } }
function Press($el) { [void][W.U5]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
function Wait-Path($filter, $t) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $t) { $f = & $filter; if ($f) { return $f }; Start-Sleep -Milliseconds 500 } }
function Shot($el, $name) {
  $r = $el.Current.BoundingRectangle; $b = New-Object Drawing.Bitmap([int]$r.Width, [int]$r.Height)
  $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size); $g.Dispose()
  $b.Save((Join-Path $env:TEMP $name)); $b.Dispose()
}
$env:__COMPAT_LAYER = 'RunAsInvoker'
$p = Start-Process "$app\varManager.exe" -WorkingDirectory $app -PassThru
try {
  $missing = Wait-El $p '^Missing' 40
  Need ([bool]$missing) 'Missing button shown'
  Start-Sleep -Seconds 4
  New-Var "$vars\E2E.Scene.1.var"
  $tidied = Wait-Path { Get-ChildItem "$vars\___VarTidied___" -Recurse -Filter 'E2E.Scene.1.var' -ErrorAction SilentlyContinue } 45
  Need ([bool]$tidied) 'watcher scanned dropped var'
  Start-Sleep -Seconds 6
  $link = "$vam\AddonPackages\___VarsLink___\E2E.Scene.1.var"
  cmd /c mklink "$link" "$($tidied.FullName)" | Out-Null
  "button after scan: $((Wait-El $p '^Missing' 5).Current.Name)"
  Shot ((Wins $p) | Select-Object -First 1) 'e2e-main.png'
  Press (Wait-El $p '^Missing' 5)
  $get = Wait-El $p '^Get all \d' 60
  "get button: $($get.Current.Name)"
  Start-Sleep -Seconds 2
  $win = (Wins $p) | Where-Object { $_.Current.Name -eq 'Missing' } | Select-Object -First 1
  if ($win) { Shot $win 'e2e-missing.png' }
  if (-not $get) { (All $p | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique | Select-Object -First 30) -join ' | ' }
  Need ([bool]$get) 'Get all button shown'
  Press $get
  $down = Wait-Path { Get-ChildItem "$vars\___VarTidied___" -Recurse -Filter 'Kamiyama Prod.*.var' -ErrorAction SilentlyContinue } 60
  Need ([bool]$down) "downloaded and tidied: $($down.Name)"
  $lk = Wait-Path { Get-ChildItem "$vam\AddonPackages\___VarsLink___" -Filter 'Kamiyama Prod.*.var' -ErrorAction SilentlyContinue } 30
  Need ([bool]$lk) "linked: $($lk.Name) ($($lk.LinkType))"
  # The chain should continue by itself. Stop it early, to keep the download small.
  $chain = Wait-El $p '^Downloaded \d+ of' 60
  Need ([bool]$chain) "chain continued: $($chain.Current.Name)"
  $cancel = Wait-El $p '^Cancel$' 5
  Need ([bool]$cancel) 'Cancel button shown'
  Press $cancel
  Start-Sleep -Seconds 12
  "button after: $((All $p | Where-Object { $_.Current.Name -match '^Missing \d' } | Select-Object -First 1).Current.Name)"
  $parts = (Get-ChildItem $vars -Filter *.part -Recurse | Measure-Object).Count
  Need ($parts -eq 0) "no leftover parts ($parts)"
  "varsMigrated left: $(Test-Path "$app\varsMigrated.txt")"
  $done = (All $p | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'ready on the Hub|Nothing|Not found|Checked|Stored' } | Select-Object -Unique) -join ' | '
  "window says: $done"
} finally {
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  Start-Sleep -Seconds 1
  if (Test-Path "$app\varManager.log") { "log errors: $((Select-String -LiteralPath "$app\varManager.log" -Pattern 'ERROR' | Measure-Object).Count)"; Select-String -LiteralPath "$app\varManager.log" -Pattern 'ERROR' | Select-Object -First 3 | ForEach-Object { $_.Line } }
  cmd /c rmdir /s /q "$base"
}
