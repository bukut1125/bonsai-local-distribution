param(
    [Parameter(Mandatory)]
    [string]$LauncherSourceRoot,
    [Parameter(Mandatory)]
    [string]$ManifestUrl,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
try { $manifestUri = [Uri]::new($ManifestUrl) } catch { throw 'ManifestUrl must be an absolute HTTPS URL.' }
if (-not $manifestUri.IsAbsoluteUri -or $manifestUri.Scheme -ne 'https' -or -not $manifestUri.AbsolutePath.EndsWith('/manifests/stable.json', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ManifestUrl must point to https://raw.githubusercontent.com/<owner>/<repo>/<branch>/manifests/stable.json.'
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$launcherRoot = [IO.Path]::GetFullPath($LauncherSourceRoot)
$launcherProject = Join-Path $launcherRoot 'desktop\LocalModelControlCenter\LocalModelControlCenter.csproj'
$launcherScripts = Join-Path $launcherRoot 'local'
$setupProject = Join-Path $projectRoot 'BonsaiSetup\BonsaiSetup.csproj'
$stagingRoot = Join-Path $projectRoot 'staging'
$launcherPublish = Join-Path $stagingRoot 'launcher-publish'
$launcherPayloadRoot = Join-Path $stagingRoot 'launcher-payload'
$launcherPayloadZip = Join-Path $stagingRoot 'BonsaiLauncherPayload.zip'
$releaseBootstrap = Join-Path $stagingRoot 'release-bootstrap.json'
$setupPublish = Join-Path $stagingRoot 'setup-publish'
$zipStage = Join-Path $stagingRoot 'portable-zip'
$distDirectory = Join-Path $projectRoot 'dist'
$setupZip = Join-Path $distDirectory 'BonsaiSetup-portable.zip'

if (-not (Test-Path -LiteralPath $launcherProject -PathType Leaf)) { throw "Local Model Control Center source not found: $launcherProject" }
foreach ($name in @('start-local-model.ps1', 'stop-local-model.ps1', 'prune-llama-log-history.ps1', 'start-hermes-local.ps1', 'sync-hermes-profile.ps1', 'set-reasoning-mode.ps1', 'normalize-local-reasoning.ps1', 'test-hermes-local.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $launcherScripts $name) -PathType Leaf)) { throw "Required launcher script missing: $name" }
}
foreach ($relative in @('config\hermes-local-template.yaml', 'hermes\reasoning\orca-reasoning-policy.json', 'hermes\reasoning\SOUL-REASONING.md', 'hermes\reasoning\model-providers\orca-local\__init__.py', 'hermes\reasoning\model-providers\orca-local\plugin.yaml', 'hermes\reasoning\plugins\orca-reasoning-scheduler\__init__.py', 'hermes\reasoning\plugins\orca-reasoning-scheduler\plugin.yaml')) {
    if (-not (Test-Path -LiteralPath (Join-Path $launcherRoot $relative) -PathType Leaf)) { throw "Required Hermes launcher asset missing: $relative" }
}
foreach ($file in @('MIT.txt', 'Apache-2.0.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "licenses\$file") -PathType Leaf)) { throw "Required license text missing: $file" }
}

foreach ($path in @($stagingRoot, $launcherPayloadRoot, $zipStage, $distDirectory)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
$payloadLauncherDirectory = Join-Path $launcherPayloadRoot 'launcher'
$payloadLocalDirectory = Join-Path $launcherPayloadRoot 'local'
$payloadHermesConfigDirectory = Join-Path $launcherPayloadRoot 'config'
$payloadHermesDirectory = Join-Path $launcherPayloadRoot 'hermes'
$payloadLicenseDirectory = Join-Path $launcherPayloadRoot 'licenses'
foreach ($path in @($payloadLauncherDirectory, $payloadLocalDirectory, $payloadHermesConfigDirectory, $payloadHermesDirectory, $payloadLicenseDirectory)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

dotnet run --project (Join-Path $projectRoot 'BonsaiSetup\BonsaiSetup.csproj') -c $Configuration -- --test-selector
if ($LASTEXITCODE -ne 0) { throw "Profile selector verification failed with exit code $LASTEXITCODE" }
dotnet run --project (Join-Path $projectRoot 'BonsaiSetup\BonsaiSetup.csproj') -c $Configuration -- --test-manifest
if ($LASTEXITCODE -ne 0) { throw "Manifest relation verification failed with exit code $LASTEXITCODE" }

dotnet publish $launcherProject -c $Configuration -r win-x64 --self-contained true -p:AssemblyName=BonsaiLauncher -p:RootNamespace=LocalModelControlCenter -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:IncludeSymbols=false -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw "Local Model Control Center publish failed with exit code $LASTEXITCODE" }
$launcherExe = Join-Path $launcherPublish 'BonsaiLauncher.exe'
if (-not (Test-Path -LiteralPath $launcherExe -PathType Leaf)) { throw "Published launcher executable is missing: $launcherExe" }
Copy-Item -LiteralPath $launcherExe -Destination $payloadLauncherDirectory -Force

foreach ($name in @('start-local-model.ps1', 'stop-local-model.ps1', 'prune-llama-log-history.ps1', 'start-hermes-local.ps1', 'sync-hermes-profile.ps1', 'set-reasoning-mode.ps1', 'normalize-local-reasoning.ps1', 'test-hermes-local.ps1')) {
    Copy-Item -LiteralPath (Join-Path $launcherScripts $name) -Destination $payloadLocalDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $launcherRoot 'config\hermes-local-template.yaml') -Destination $payloadHermesConfigDirectory -Force
foreach ($relative in @('hermes\reasoning\orca-reasoning-policy.json', 'hermes\reasoning\SOUL-REASONING.md', 'hermes\reasoning\model-providers\orca-local\__init__.py', 'hermes\reasoning\model-providers\orca-local\plugin.yaml', 'hermes\reasoning\plugins\orca-reasoning-scheduler\__init__.py', 'hermes\reasoning\plugins\orca-reasoning-scheduler\plugin.yaml')) {
    $destination = Join-Path $launcherPayloadRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcherRoot $relative) -Destination $destination -Force
}
& (Join-Path $projectRoot 'scripts\Test-HermesProfile.ps1') -LauncherSourceRoot $launcherPayloadRoot -DistributionRoot $projectRoot
if ($LASTEXITCODE -ne 0) { throw "Hermes profile/Windows PowerShell 5.1 verification failed with exit code $LASTEXITCODE" }
foreach ($name in @('MIT.txt', 'Apache-2.0.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "licenses\$name") -Destination $payloadLicenseDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $launcherPayloadRoot -Force
Compress-Archive -Path (Join-Path $launcherPayloadRoot '*') -DestinationPath $launcherPayloadZip -CompressionLevel Optimal -Force

$bootstrap = [ordered]@{
    schema_version = 1
    manifest_url = $ManifestUrl
    manifest_channel = 'stable'
    default_install_dir = '%LOCALAPPDATA%\BonsaiLocal'
}
$bootstrapJson = $bootstrap | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($releaseBootstrap, $bootstrapJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:BootstrapFile=$releaseBootstrap" -o $setupPublish
if ($LASTEXITCODE -ne 0) { throw "BonsaiSetup publish failed with exit code $LASTEXITCODE" }
$setupExe = Join-Path $setupPublish 'BonsaiSetup.exe'
if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) { throw "Published setup executable is missing: $setupExe" }
& $setupExe --test-hermes
if ($LASTEXITCODE -ne 0) { throw "Hermes installer provenance/PATH verification failed with exit code $LASTEXITCODE" }
& (Join-Path $projectRoot 'scripts\Test-AgentMcp.ps1') -SetupExecutable $setupExe -DistributionRoot $projectRoot
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $distDirectory 'BonsaiSetup.exe') -Force
Copy-Item -LiteralPath $releaseBootstrap -Destination (Join-Path $zipStage 'bootstrap.json') -Force
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $zipStage 'BonsaiSetup.exe') -Force
Compress-Archive -Path (Join-Path $zipStage '*') -DestinationPath $setupZip -CompressionLevel Optimal -Force

Get-Item -LiteralPath (Join-Path $distDirectory 'BonsaiSetup.exe'), $setupZip | Select-Object FullName,Length
