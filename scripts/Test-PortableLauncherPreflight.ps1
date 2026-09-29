param(
    [Parameter(Mandatory)] [string]$LauncherSourceRoot,
    [Parameter(Mandatory)] [string]$ModelSourceRoot,
    [Parameter(Mandatory)] [string]$RuntimeSourceRoot,
    [string]$ProfileId = 'orca-bonsai-27b-uncensored-8gb-stable'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$launcherRoot = [IO.Path]::GetFullPath($LauncherSourceRoot)
$modelSource = [IO.Path]::GetFullPath($ModelSourceRoot)
$runtimeSource = [IO.Path]::GetFullPath($RuntimeSourceRoot)
foreach ($path in @($launcherRoot, $modelSource, $runtimeSource)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Required test source directory is missing: $path" }
}

$sandbox = Join-Path (Join-Path $projectRoot 'staging') ('portable-preflight-' + [Guid]::NewGuid().ToString('N'))
$dirs = @('config', 'local', 'runtime\llama-local', 'runtime\llama-prism', 'logs\llama', 'records', 'models')
foreach ($relative in $dirs) { New-Item -ItemType Directory -Path (Join-Path $sandbox $relative) -Force | Out-Null }

$modelLink = Join-Path $sandbox 'models\OrcaBonsai-27B-Uncensored'
$runtimeLink = Join-Path $sandbox 'runtime\llama-prism\prism-b10709-9a9394a'
New-Item -ItemType Junction -Path $modelLink -Target $modelSource | Out-Null
New-Item -ItemType Junction -Path $runtimeLink -Target $runtimeSource | Out-Null

foreach ($name in @('start-local-model.ps1', 'stop-local-model.ps1', 'prune-llama-log-history.ps1')) {
    Copy-Item -LiteralPath (Join-Path $launcherRoot "local\$name") -Destination (Join-Path $sandbox 'local') -Force
}
Copy-Item -LiteralPath (Join-Path $launcherRoot 'config\local-runtime.json') -Destination (Join-Path $sandbox 'config\local-runtime.json') -Force

$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'manifests\stable.json') -Raw | ConvertFrom-Json
$hardwareProfiles = Get-Content -LiteralPath (Join-Path $projectRoot 'profiles\hardware-profiles.json') -Raw | ConvertFrom-Json
$selectedHardwareProfile = @($hardwareProfiles.profiles | Where-Object { $_.id -eq 'nvidia_8gb' }) | Select-Object -First 1
if ($null -eq $selectedHardwareProfile) { throw 'Test hardware profile nvidia_8gb is missing.' }
$manifest.model_registry.canonical_model_root = (Join-Path $sandbox 'models')
$manifest.model_registry.default_model_id = 'orca-bonsai-27b'
$manifest.model_registry.default_profile_id = $ProfileId
$allowedProfileIds = @($selectedHardwareProfile.selectable_profile_ids) + @($selectedHardwareProfile.fallback_profile_ids) + @($ProfileId) | Sort-Object -Unique
$manifest.model_registry.profiles = @($manifest.model_registry.profiles | Where-Object { $_.id -in $allowedProfileIds })
if ($manifest.model_registry.profiles.Count -gt 6) { throw 'Portable registry contains too many profile choices for an ordinary user.' }
$registryPath = Join-Path $sandbox 'config\model-registry.json'
[IO.File]::WriteAllText($registryPath, ($manifest.model_registry | ConvertTo-Json -Depth 30) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$selected = @($manifest.model_registry.profiles | Where-Object { $_.id -eq $ProfileId })
if ($selected.Count -ne 1) { throw "Test profile must resolve exactly once: $ProfileId" }
$result = & (Join-Path $sandbox 'local\start-local-model.ps1') -ModelId 'orca-bonsai-27b' -ProfileId $ProfileId -PreflightOnly | ConvertFrom-Json
if ([string]$result.status -ne 'preflight_passed') { throw "Portable launcher preflight failed: $($result | ConvertTo-Json -Depth 8 -Compress)" }
if ([string]$result.model_path -notlike "$(Join-Path $sandbox 'models')*") { throw "Portable model root was not honored: $($result.model_path)" }
if (@($result.arguments | Where-Object { $_ -eq '--gpu-layers' }).Count -ne 1) { throw 'Profile CLI adapter did not emit GPU layer setting.' }
if (@($result.arguments | Where-Object { $_ -eq '24' }).Count -lt 1) { throw '8 GB hybrid profile GPU layer value did not reach the runtime command.' }

[pscustomobject]@{
    status = 'passed'
    profile_id = $ProfileId
    portable_model_root = (Join-Path $sandbox 'models')
    backend = $result.backend_id
    model_path_resolved_inside_install_root = $true
    device_preflight_executed = $true
    server_process_started = $false
    model_process_started = $false
    sandbox = $sandbox
} | ConvertTo-Json -Depth 4
