<#
.SYNOPSIS
    Publishes PaceApp and builds the Inno Setup installer.
.DESCRIPTION
    Runs dotnet publish for win-x64 self-contained, then invokes ISCC to
    compile the installer. The resulting Setup.exe is placed in installer\Output\.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $repoRoot) { $repoRoot = $PSScriptRoot }

$projectPath = Join-Path $repoRoot 'src\PaceApp.App\PaceApp.App.csproj'
$publishDir  = Join-Path $repoRoot 'published\PaceCoach-win-x64'
$issPath     = Join-Path $repoRoot 'installer\PaceCoach.iss'

Write-Host '--- Publishing PaceApp (win-x64, self-contained) ---' -ForegroundColor Cyan
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "`n--- Building installer ---" -ForegroundColor Cyan
$iscc = Get-Command 'iscc' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source

if (-not $iscc) {
    # Try common Inno Setup install locations
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}

if (-not $iscc) {
    Write-Warning 'Inno Setup (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php'
    Write-Warning "Once installed, re-run this script or run:  iscc `"$issPath`""
    exit 1
}

& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$outputExe = Join-Path $repoRoot 'installer\Output\PaceCoach-Setup.exe'
Write-Host "`nInstaller ready: $outputExe" -ForegroundColor Green
