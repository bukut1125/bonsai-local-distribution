[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SetupExecutable,

    [string]$DistributionRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$setupPath = [IO.Path]::GetFullPath($SetupExecutable)
$projectRoot = [IO.Path]::GetFullPath($DistributionRoot)
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "BonsaiSetup.exe not found: $setupPath" }

$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'manifests\stable.json') -Raw | ConvertFrom-Json
$fixtureRoot = Join-Path $projectRoot ('staging\agent-mcp-' + [guid]::NewGuid().ToString('N'))
$configRoot = Join-Path $fixtureRoot 'config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null

$registry = [ordered]@{
    schema_version = 2
    application = 'Bonsai MCP test fixture'
    canonical_model_root = Join-Path $fixtureRoot 'models'
    endpoint = [ordered]@{ host = '127.0.0.1'; port = 18080 }
    default_model_id = 'fixture-model'
    default_profile_id = 'fixture-profile'
    backends = @(
        [ordered]@{ id = 'llama-prism-b10709-cuda'; display_name = 'Fixture CUDA'; executable_relative_path = 'runtime\fixture\llama-server.exe'; capabilities = @('gguf', 'cuda') }
    )
    models = @(
        [ordered]@{ id = 'fixture-model'; display_name = 'Fixture GGUF'; source_repository = 'fixture/source'; weight_format = 'GGUF'; quantization = 'Q4_K_M' }
    )
    profiles = @(
        [ordered]@{ id = 'fixture-profile'; display_name = 'Fixture profile'; model_id = 'fixture-model'; backend_id = 'llama-prism-b10709-cuda'; context_size = 8192; gpu_layers = 'all' }
    )
}
[IO.File]::WriteAllText((Join-Path $configRoot 'model-registry.json'), ($registry | ConvertTo-Json -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $configRoot 'model-extension.json'), ($manifest.model_extension | ConvertTo-Json -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $configRoot 'hardware.json'), (@{ os = @{ name = 'fixture' }; gpus = @(); memory = @{ total_bytes = 16000000000; available_bytes = 8000000000 } } | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$messages = @(
    @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'Bonsai MCP test'; version = '1' } } },
    @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} },
    @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} },
    @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'bonsai_get_hardware'; arguments = @{} } },
    @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'bonsai_list_models'; arguments = @{} } },
    @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'bonsai_search_huggingface_gguf'; arguments = @{ query = 'TinyLlama' } } },
    @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'bonsai_inspect_huggingface_gguf'; arguments = @{ repo_id = 'TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF' } } },
    @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'bonsai_install_huggingface_gguf'; arguments = @{ repo_id = 'TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF'; file_name = 'tinyllama-1.1b-chat-v1.0.Q2_K.gguf'; display_name = 'Invalid fixture'; backend_id = 'llama-prism-b10709-cuda'; context_size = 1; gpu_layers = 'all' } } },
    @{ jsonrpc = '2.0'; id = 8; method = 'server/discover'; params = @{ _meta = @{ 'io.modelcontextprotocol/protocolVersion' = '2026-07-28'; 'io.modelcontextprotocol/clientInfo' = @{ name = 'Bonsai MCP test'; version = '1' }; 'io.modelcontextprotocol/clientCapabilities' = @{} } } },
    @{ jsonrpc = '2.0'; id = 9; method = 'tools/list'; params = @{ _meta = @{ 'io.modelcontextprotocol/protocolVersion' = '2026-07-28'; 'io.modelcontextprotocol/clientCapabilities' = @{} } } },
    @{ jsonrpc = '2.0'; id = 10; method = 'tools/call'; params = @{ name = 'bonsai_list_models'; arguments = @{}; _meta = @{ 'io.modelcontextprotocol/protocolVersion' = '2026-07-28'; 'io.modelcontextprotocol/clientCapabilities' = @{} } } },
    @{ jsonrpc = '2.0'; id = 11; method = 'shutdown'; params = @{} }
)

$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = $setupPath
$processInfo.WorkingDirectory = $fixtureRoot
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$processInfo.RedirectStandardInput = $true
$processInfo.RedirectStandardOutput = $true
$processInfo.RedirectStandardError = $true
$processInfo.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$processInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$processInfo.ArgumentList.Add('--mcp')
$processInfo.ArgumentList.Add('--install-dir')
$processInfo.ArgumentList.Add($fixtureRoot)

$process = [Diagnostics.Process]::new()
$process.StartInfo = $processInfo
if (-not $process.Start()) { throw 'BonsaiSetup MCP process did not start.' }
foreach ($message in $messages) { $process.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 20 -Compress)) }
$process.StandardInput.Close()
$stdout = $process.StandardOutput.ReadToEnd()
$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "MCP process failed ($($process.ExitCode)): $stderr`n$stdout" }

$responses = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
if ($responses.Count -ne 11) { throw "Expected 11 MCP responses, got $($responses.Count). Raw stdout: $stdout" }
if ($responses[0].id -ne 1 -or $responses[0].result.protocolVersion -ne '2024-11-05') { throw 'MCP initialize contract failed.' }
$toolNames = @($responses[1].result.tools | ForEach-Object name)
foreach ($requiredTool in @('bonsai_get_hardware', 'bonsai_list_models', 'bonsai_search_huggingface_gguf', 'bonsai_inspect_huggingface_gguf', 'bonsai_install_huggingface_gguf')) {
    if ($requiredTool -notin $toolNames) { throw "MCP tools/list omitted $requiredTool." }
}
$hardware = $responses[2].result.content[0].text | ConvertFrom-Json
if ($hardware.memory.total_bytes -le 0 -or $hardware.os.name -ne 'fixture') { throw 'bonsai_get_hardware fixture result mismatch.' }
$modelList = $responses[3].result.content[0].text | ConvertFrom-Json
if ($modelList.models.Count -ne 1 -or $modelList.models[0].id -ne 'fixture-model') { throw 'bonsai_list_models fixture result mismatch.' }
$search = $responses[4].result.content[0].text | ConvertFrom-Json
if (@($search.results).Count -lt 1) { throw 'Hugging Face GGUF search returned no results.' }
$inspect = $responses[5].result.content[0].text | ConvertFrom-Json
$selected = @($inspect.files | Where-Object file_name -EQ 'tinyllama-1.1b-chat-v1.0.Q2_K.gguf') | Select-Object -First 1
if ($null -eq $selected -or $selected.size_bytes -le 0 -or $selected.sha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'Hugging Face inspect metadata did not return exact GGUF size and LFS SHA-256.'
}
if (-not $responses[6].result.isError -or (Test-Path -LiteralPath (Join-Path $configRoot 'user-model-registry.json'))) {
    throw 'Invalid context size was not rejected before model download/registration.'
}
$discovery = $responses[7].result
if ($discovery.resultType -ne 'complete' -or '2026-07-28' -notin $discovery.supportedVersions -or $discovery._meta.'io.modelcontextprotocol/serverInfo'.name -ne 'bonsai-model-integration') {
    throw 'MCP modern server/discover response mismatch.'
}
if ($responses[8].result.resultType -ne 'complete' -or $responses[8].result.cacheScope -ne 'private' -or @($responses[8].result.tools).Count -ne $toolNames.Count) {
    throw 'MCP modern tools/list cache metadata or tools mismatch.'
}
$modernModelList = $responses[9].result.content[0].text | ConvertFrom-Json
if ($responses[9].result.resultType -ne 'complete' -or $modernModelList.models[0].id -ne 'fixture-model') { throw 'MCP modern tools/call result mismatch.' }

"PASS · MCP legacy and 2026-07-28 modern lifecycles · $($toolNames.Count) tools · search=$(@($search.results).Count) repos · inspect size=$($selected.size_bytes) SHA-256=$($selected.sha256) · invalid install rejected before download"
