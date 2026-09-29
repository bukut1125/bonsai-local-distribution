param(
    [Parameter(Mandatory)]
    [string]$LauncherRoot
)

$ErrorActionPreference = 'Stop'
$launcherPaths = @(
    (Join-Path $LauncherRoot 'local\start-local-model.ps1'),
    (Join-Path $LauncherRoot 'local\stop-local-model.ps1'),
    (Join-Path $LauncherRoot 'local\prune-llama-log-history.ps1')
)
$projectScripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1' | Select-Object -ExpandProperty FullName)
$paths = @($launcherPaths) + @($projectScripts)

$failed = 0
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Output "FAIL · missing $path"
        $failed++
        continue
    }

    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        Write-Output "FAIL · $path"
        foreach ($errorItem in $errors) { Write-Output "  $($errorItem.Message) at $($errorItem.Extent.StartLineNumber):$($errorItem.Extent.StartColumnNumber)" }
        $failed++
    } else {
        Write-Output "PASS · $path parses in PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
    }
}

if ($failed -gt 0) { exit 1 }
exit 0
