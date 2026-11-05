# Builds the new app. By default it uses the last commit, so a release is always something in git.
# With -Local it uses the working folder as it is, uncommitted changes included.
param([switch]$Local)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo

$commit = (git rev-parse --short HEAD).Trim()
$changes = @(git status --porcelain)
if ($Local) {
    $name = 'varManager-local'
    $root = $repo
    Write-Host "Building the working folder on top of $commit, with $($changes.Count) uncommitted change(s)."
}
else {
    $name = "varManager-$commit"
    $root = Join-Path $env:TEMP "varManager-release-$commit"
    if ($changes.Count -gt 0) {
        Write-Warning "$($changes.Count) uncommitted change(s) are NOT in this release:"
        $changes | ForEach-Object { Write-Host "  $_" }
    }
}

$source = "$root.zip"

function Remove-Export {
    if ($Local) { return }
    if (Test-Path $root) { cmd /c rmdir /s /q "`"$root`"" }
    if (Test-Path $source) { Remove-Item $source -Force }
}

Remove-Export
try {
    if (-not $Local) {
        Write-Host "Exporting $commit"
        git archive --format=zip -o $source HEAD varManager.App varManager.Core tests varManager/varManager.mdb
        if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
        Expand-Archive $source $root
    }

    Write-Host 'Running tests'
    Push-Location (Join-Path $root 'tests\varManager.App.Tests')
    dotnet test
    $tests = $LASTEXITCODE
    Pop-Location
    if ($tests -ne 0) { throw 'Tests failed, so nothing was released.' }

    # Built straight into an empty folder, so nothing left in bin can reach the release.
    Write-Host 'Building'
    $out = Join-Path $repo "release\$name"
    if (Test-Path $out) { cmd /c rmdir /s /q "`"$out`"" }
    dotnet build (Join-Path $root 'varManager.App\varManager.App.csproj') -c Release -o $out
    if ($LASTEXITCODE -ne 0) { throw 'The build failed, so nothing was released.' }
    Get-ChildItem $out -Filter '*.pdb' -Recurse | Remove-Item -Force

    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive (Join-Path $out '*') $zip
    Write-Host "Done: $out"
    Write-Host "Done: $zip"
}
finally {
    Remove-Export
}
