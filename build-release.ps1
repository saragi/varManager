# Builds the new app from the last commit, so a release is always something in git.
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo

$commit = (git rev-parse --short HEAD).Trim()
$name = "varManager-$commit"
$changes = @(git status --porcelain)
if ($changes.Count -gt 0) {
    Write-Warning "$($changes.Count) uncommitted change(s) are NOT in this release:"
    $changes | ForEach-Object { Write-Host "  $_" }
}

$work = Join-Path $env:TEMP "varManager-release-$commit"
$source = "$work.zip"
if (Test-Path $work) { cmd /c rmdir /s /q "`"$work`"" }
if (Test-Path $source) { Remove-Item $source -Force }

try {
    Write-Host "Exporting $commit"
    git archive --format=zip -o $source HEAD varManager.App varManager.Core tests varManager/varManager.mdb
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
    Expand-Archive $source $work

    Write-Host 'Running tests'
    Push-Location (Join-Path $work 'tests\varManager.App.Tests')
    dotnet test
    $tests = $LASTEXITCODE
    Pop-Location
    if ($tests -ne 0) { throw 'Tests failed, so nothing was released.' }

    Write-Host 'Building'
    dotnet build (Join-Path $work 'varManager.App\varManager.App.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'The build failed, so nothing was released.' }

    $out = Join-Path $repo "release\$name"
    if (Test-Path $out) { cmd /c rmdir /s /q "`"$out`"" }
    New-Item -ItemType Directory -Force $out | Out-Null
    Copy-Item (Join-Path $work 'varManager.App\bin\Release\net10.0-windows\*') $out -Recurse -Exclude '*.pdb'

    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive (Join-Path $out '*') $zip
    Write-Host "Done: $out"
    Write-Host "Done: $zip"
}
finally {
    if (Test-Path $work) { cmd /c rmdir /s /q "`"$work`"" }
    if (Test-Path $source) { Remove-Item $source -Force }
}
