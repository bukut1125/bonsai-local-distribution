param(
    [Parameter(Mandatory)] [string]$LauncherSourceRoot,
    [Parameter(Mandatory)] [string]$DistributionRoot
)

$ErrorActionPreference = 'Stop'
$launcherRoot = [IO.Path]::GetFullPath($LauncherSourceRoot)
$distributionRoot = [IO.Path]::GetFullPath($DistributionRoot)
$manifest = Get-Content -Raw -LiteralPath (Join-Path $distributionRoot 'manifests\stable.json') | ConvertFrom-Json
$profiles = @($manifest.model_registry.profiles | Where-Object { [string]$_.model_id -eq [string]$manifest.model_registry.default_model_id })
$stableProfile = $profiles | Where-Object { [string]$_.id -eq [string]$manifest.model_registry.default_profile_id } | Select-Object -First 1
if ($null -eq $stableProfile) { throw 'Default Hermes profile fixture is missing.' }
$longProfile = $profiles | Where-Object { [int]$_.context_size -gt [int]$stableProfile.context_size } | Sort-Object { [int]$_.context_size } | Select-Object -First 1
if ($null -eq $longProfile) { throw 'Long-context Hermes profile fixture is missing.' }

$powerShell51 = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $powerShell51 -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for this launcher compatibility check.' }
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('BonsaiHermesProfileTest-' + [guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Hermes test root escaped the Windows temp directory.' }

function Write-TestJson {
    param([string]$Path, [object]$Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $json = $Value | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

try {
    foreach ($relative in @('local\start-hermes-local.ps1', 'local\sync-hermes-profile.ps1', 'local\set-reasoning-mode.ps1', 'local\normalize-local-reasoning.ps1', 'local\test-hermes-local.ps1')) {
        $destination = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $launcherRoot $relative) -Destination $destination -Force
    }
    foreach ($relative in @('config\hermes-local-template.yaml', 'hermes\reasoning\orca-reasoning-policy.json', 'hermes\reasoning\SOUL-REASONING.md', 'hermes\reasoning\model-providers\orca-local\__init__.py', 'hermes\reasoning\model-providers\orca-local\plugin.yaml', 'hermes\reasoning\plugins\orca-reasoning-scheduler\__init__.py', 'hermes\reasoning\plugins\orca-reasoning-scheduler\plugin.yaml')) {
        $destination = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $launcherRoot $relative) -Destination $destination -Force
    }

    $parserScript = Join-Path $testRoot 'parse-launcher-scripts.ps1'
    $parserBody = @'
param([Parameter(Mandatory)] [string]$Root)
$ErrorActionPreference = 'Stop'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Root 'local') -Filter '*.ps1' -File) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ($file.Name + ': ' + (($errors | ForEach-Object Message) -join '; ')) }
}
Write-Output 'PASS · packaged launcher scripts parse in Windows PowerShell 5.1'
'@
    [IO.File]::WriteAllText($parserScript, $parserBody, [Text.UTF8Encoding]::new($false))
    & $powerShell51 -NoProfile -ExecutionPolicy Bypass -File $parserScript $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Packaged launcher script parsing failed on Windows PowerShell 5.1.' }

    Write-TestJson (Join-Path $testRoot 'config\model-registry.json') $manifest.model_registry
    Write-TestJson (Join-Path $testRoot 'config\model-extension.json') $manifest.model_extension
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'updater') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $testRoot 'updater\BonsaiSetup.exe'), [byte[]](0x4D, 0x5A))

    $userPathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
    $contexts = [System.Collections.Generic.List[int]]::new()
    foreach ($profile in @($stableProfile, $longProfile)) {
        Write-TestJson (Join-Path $testRoot 'config\launcher-state.json') ([ordered]@{
            schema_version = 1
            selected_model_id = [string]$manifest.model_registry.default_model_id
            selected_profile_id = [string]$profile.id
        })
        & $powerShell51 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $testRoot 'local\sync-hermes-profile.ps1')
        if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell 5.1 profile sync failed for $($profile.id)." }
        & $powerShell51 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $testRoot 'local\set-reasoning-mode.ps1') -Mode normal_agent -Reason 'Windows PowerShell 5.1 fixture'
        if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell 5.1 reasoning-mode write failed for $($profile.id)." }

        $configPath = Join-Path $testRoot 'runtime\hermes-local\config.yaml'
        $yaml = (New-Object Text.UTF8Encoding($false, $true)).GetString([IO.File]::ReadAllBytes($configPath))
        if ($yaml -match '__[A-Z0-9_]+__') { throw 'Hermes YAML still contains an unresolved template token.' }
        $contextMatch = [regex]::Match($yaml, '(?m)^  context_length: (\d+)$')
        if (-not $contextMatch.Success) { throw 'Hermes config did not render context_length.' }
        $contexts.Add([int]$contextMatch.Groups[1].Value)
        if (-not $yaml.Contains('base_url: http://127.0.0.1:18080/v1')) { throw 'Hermes config is not connected to the local Bonsai API.' }
        if (-not $yaml.Contains('mcp_servers:') -or -not $yaml.Contains('bonsai_model_extension:')) { throw 'Hermes MCP model-extension connection is missing.' }
        if (-not $yaml.Contains('trust: full')) { throw 'Hermes MCP model-extension trust setting is missing.' }
        if (-not $yaml.Contains((ConvertTo-Json -InputObject $testRoot -Compress))) { throw 'Hermes MCP install root was not quoted into the profile config.' }
        foreach ($tool in @('bonsai_get_hardware', 'bonsai_list_models', 'bonsai_search_huggingface_gguf', 'bonsai_inspect_huggingface_gguf', 'bonsai_install_huggingface_gguf')) {
            if (-not $yaml.Contains($tool)) { throw "Hermes MCP tool missing from config: $tool" }
        }
        $modePath = Join-Path $testRoot 'runtime\hermes-local\reasoning-scheduler\next-mode.json'
        $modeBytes = [IO.File]::ReadAllBytes($modePath)
        if ($modeBytes.Length -ge 3 -and $modeBytes[0] -eq 239 -and $modeBytes[1] -eq 187 -and $modeBytes[2] -eq 191) { throw 'Windows PowerShell 5.1 reasoning state unexpectedly has a UTF-8 BOM.' }
        if ((Get-Content -Raw -LiteralPath $modePath | ConvertFrom-Json).mode -ne 'normal_agent') { throw 'Windows PowerShell 5.1 reasoning mode state did not round-trip.' }
    }

    if ($contexts[1] -le $contexts[0]) { throw "Context profile switch did not increase Hermes context: $($contexts[0]) -> $($contexts[1])." }
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -cne $userPathBefore) { throw 'Profile synchronization changed user PATH.' }
    Write-Output "PASS · Windows PowerShell 5.1 Hermes profile and reasoning-state writes · context $($contexts[0]) -> $($contexts[1]) · Bonsai API/MCP paths · user PATH unchanged"
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($tempBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside Windows temp directory.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
