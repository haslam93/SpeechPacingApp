param(
    [switch]$Tray,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProjectPath = Join-Path $projectRoot 'src\PaceApp.App\PaceApp.App.csproj'
$exePath = Join-Path $projectRoot "src\PaceApp.App\bin\$Configuration\net10.0-windows\PaceApp.App.exe"

Push-Location $projectRoot
try {
    if (-not $Tray) {
        Get-Process -Name 'PaceApp.App' -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Stop-Process -Id $_.Id -Force -ErrorAction Stop
            }
            catch {
            }
        }
    }

    dotnet build $appProjectPath -c $Configuration | Out-Host

    if ($LASTEXITCODE -ne 0) {
        dotnet clean $appProjectPath -c $Configuration | Out-Host
        dotnet build $appProjectPath -c $Configuration | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'PaceApp build failed. See the output above for details.'
        }
    }

    if (-not (Test-Path $exePath)) {
        throw "Could not find built executable at $exePath"
    }

    $arguments = @()
    if ($Tray) {
        $arguments += '--tray'
    }

    if ($arguments.Count -gt 0) {
        Start-Process -FilePath $exePath -ArgumentList $arguments | Out-Null
    }
    else {
        Start-Process -FilePath $exePath | Out-Null
    }
}
finally {
    Pop-Location
}