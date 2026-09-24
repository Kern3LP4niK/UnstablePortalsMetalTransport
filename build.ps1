param(
    [string]$ValheimPath = $env:VALHEIM_INSTALL,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ValheimPath)) {
    $ValheimPath = Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Valheim"
}

$requiredFiles = @(
    (Join-Path $ValheimPath "BepInEx\core\BepInEx.dll"),
    (Join-Path $ValheimPath "BepInEx\core\0Harmony.dll"),
    (Join-Path $ValheimPath "valheim_Data\Managed\UnityEngine.dll")
)

$missingFiles = $requiredFiles | Where-Object { -not (Test-Path $_) }
if ($missingFiles.Count -gt 0) {
    throw "Valheim/BepInEx references were not found. Install the current BepInExPack for Valheim or pass -ValheimPath. Missing: $($missingFiles -join ', ')"
}

$projectPath = Join-Path $PSScriptRoot "UnstablePortalsMetalTransport.csproj"
dotnet build $projectPath -c $Configuration -p:VALHEIM_INSTALL="$ValheimPath"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

$dllPath = Join-Path $PSScriptRoot "bin\$Configuration\net462\UnstablePortalsMetalTransport.dll"
Write-Host "Built: $dllPath"
