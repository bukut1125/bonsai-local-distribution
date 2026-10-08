using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BonsaiSetup.Distribution;

internal sealed class HermesProvisioner(ResumableDownloader downloader)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(90);

    public async Task<HermesInstallationPlan> InspectAsync(
        HermesAgentSettings settings,
        string installRoot,
        bool update,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(settings, installRoot, cancellationToken).ConfigureAwait(false);
        var previous = ReadReceipt(installRoot);
        var existingIsManaged = existing is not null && IsInsideRoot(installRoot, existing.Path)
                                && (previous?["managed_by_bonsai"]?.GetValue<bool>() == true
                                    || File.Exists(Path.Combine(installRoot, settings.InstallDirectoryRelativePath, "scripts", "install.ps1")));
        var previousCommit = previous?["source_commit"]?.GetValue<string>() ?? "";
        var needsManagedUpdate = update && existingIsManaged && !string.Equals(previousCommit, settings.SourceCommit, StringComparison.OrdinalIgnoreCase);
        var needsInstall = existing is null || needsManagedUpdate;
        return new HermesInstallationPlan(
            existing?.Path,
            existing?.Version ?? "",
            existingIsManaged,
            needsInstall,
            needsInstall ? settings.EstimatedInstallBytes : 0,
            needsManagedUpdate,
            previousCommit);
    }

    public async Task<HermesInstallationResult> EnsureInstalledAsync(
        DistributionManifest manifest,
        string installRoot,
        HermesInstallationPlan plan,
        JsonObject? previousAssetReceipt,
        IDictionary<string, DownloadAsset> installedAssets,
        CancellationToken cancellationToken)
    {
        var settings = manifest.HermesAgent;
        if (!plan.NeedsInstall && plan.ExistingBinaryPath is not null)
        {
            Console.WriteLine($"已找到可用 Hermes Agent，沿用：{plan.ExistingBinaryPath}");
            var sourceCommit = plan.ManagedByBonsai ? plan.PreviousCommit : "";
            var result = new HermesInstallationResult(plan.ExistingBinaryPath, plan.Version, plan.ManagedByBonsai, sourceCommit);
            WriteReceipt(installRoot, result, manifest.Version);
            return result;
        }

        var asset = manifest.GetAsset(settings.BootstrapAssetId);
        Console.WriteLine(plan.IsManagedUpdate
            ? $"更新 Bonsai 管理的 Hermes Agent 至 {settings.SourceCommit[..12]}。"
            : $"正在安裝 Hermes Agent {settings.SourceCommit[..12]}（不需要管理員權限）。");
        await downloader.DownloadAsync(
            asset,
            ResolveInsideRoot(installRoot, asset.TargetRelativePath),
            cancellationToken,
            ReceiptMatches(previousAssetReceipt, asset)).ConfigureAwait(false);
        installedAssets[asset.Id] = asset;

        var home = ResolveInsideRoot(installRoot, settings.HomeRelativePath);
        var installDirectory = ResolveInsideRoot(installRoot, settings.InstallDirectoryRelativePath);
        var upstreamScript = ResolveInsideRoot(installRoot, asset.TargetRelativePath);
        var script = upstreamScript + ".bonsai.ps1";
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(installDirectory);

        var upstreamText = await File.ReadAllTextAsync(upstreamScript, cancellationToken).ConfigureAwait(false);
        var pathFreeText = CreatePathFreeInstallerScript(upstreamText);
        await File.WriteAllTextAsync(script, pathFreeText, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var processResult = await RunOfficialInstallerAsync(
            script,
            settings,
            home,
            installDirectory,
            cancellationToken).ConfigureAwait(false);
        if (processResult.ExitCode != 0)
            throw new InvalidOperationException("Hermes Agent 安裝失敗：" + BuildError(processResult));

        var executable = ResolveInsideRoot(installRoot, settings.ExecutableRelativePath);
        var version = await ReadHermesVersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (version is null)
            throw new InvalidOperationException("Hermes 安裝程序結束，但 hermes.exe --version 未成功；安裝狀態不會標記完成。");

        var installed = new HermesInstallationResult(executable, version, true, settings.SourceCommit);
        WriteReceipt(installRoot, installed, manifest.Version);
        Console.WriteLine($"Hermes Agent 已就緒：{version} · source {settings.SourceCommit[..12]}");
        return installed;
    }

    internal static string CreatePathFreeInstallerScript(string upstreamScript)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(upstreamScript, @"(?m)^[ \t]*Set-LauncherUserPath[ \t]+\$binDir[ \t]*$");
        if (matches.Count != 1) throw new InvalidDataException("Pinned Hermes installer changed its PATH hook; refusing to run an unreviewed variant.");
        var patched = System.Text.RegularExpressions.Regex.Replace(
            upstreamScript,
            @"(?m)^([ \t]*)Set-LauncherUserPath[ \t]+\$binDir[ \t]*$",
            "$1# Bonsai: keep PATH unchanged; the launcher calls hermes.exe by absolute path.");
        if (System.Text.RegularExpressions.Regex.IsMatch(patched, @"(?m)^[ \t]*Set-LauncherUserPath[ \t]+\$binDir[ \t]*$"))
            throw new InvalidDataException("Hermes installer PATH hook remained active after the Bonsai patch.");
        return patched;
    }

    public static void ApplyToLauncherRuntime(JsonObject localRuntime, HermesInstallationResult installation)
    {
        var hermes = localRuntime["hermes"] as JsonObject
                     ?? throw new InvalidDataException("manifest local_runtime.hermes 缺少 launcher 設定。");
        hermes["binary_path"] = installation.BinaryPath;
    }

    private static async Task<HermesExistingBinary?> FindExistingAsync(
        HermesAgentSettings settings,
        string installRoot,
        CancellationToken cancellationToken)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(installRoot, settings.ExecutableRelativePath),
            Path.Combine(localAppData, "hermes", "bin", "hermes.exe"),
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "hermes.exe")
        };

        var existingConfig = Path.Combine(installRoot, "config", "local-runtime.json");
        if (File.Exists(existingConfig))
        {
            try
            {
                var configured = JsonNode.Parse(await File.ReadAllTextAsync(existingConfig, cancellationToken).ConfigureAwait(false))?["hermes"]?["binary_path"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(configured)) candidates.Insert(0, configured);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            {
                Console.WriteLine("既有 Hermes 設定無法解析，改用標準安裝位置偵測。");
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entry.Contains("hermes", StringComparison.OrdinalIgnoreCase)
                && !entry.Contains("BonsaiLocal", StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(Path.Combine(entry.Trim('"'), "hermes.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath;
            try { fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!File.Exists(fullPath)) continue;
            var version = await ReadHermesVersionAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (version is not null) return new HermesExistingBinary(fullPath, version);
        }

        return null;
    }

    private static async Task<string?> ReadHermesVersionAsync(string binaryPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(binaryPath)) return null;
        var result = await RunProcessAsync(binaryPath, ["--version"], Path.GetDirectoryName(binaryPath)!, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        var output = string.Join(" ", new[] { result.Stdout, result.Stderr }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return output.Length == 0 ? "Hermes Agent (version output unavailable)" : output;
    }

    private static async Task<ProcessResult> RunOfficialInstallerAsync(
        string scriptPath,
        HermesAgentSettings settings,
        string home,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var systemPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executable = File.Exists(systemPowerShell) ? systemPowerShell : "powershell.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Branch");
        startInfo.ArgumentList.Add("main");
        startInfo.ArgumentList.Add("-Commit");
        startInfo.ArgumentList.Add(settings.SourceCommit);
        startInfo.ArgumentList.Add("-HermesHome");
        startInfo.ArgumentList.Add(home);
        startInfo.ArgumentList.Add("-InstallDir");
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add("-NonInteractive");
        if (settings.SkipBrowserTools) startInfo.ArgumentList.Add("-SkipBrowser");
        if (settings.SkipComputerUseTools) startInfo.ArgumentList.Add("-SkipComputerUse");
        startInfo.ArgumentList.Add("-Verbose");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("無法啟動 Windows PowerShell 以安裝 Hermes Agent。");
        var output = new List<string>();
        var errors = new List<string>();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (output) output.Add(eventArgs.Data);
            Console.WriteLine("[Hermes] " + eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (errors) errors.Add(eventArgs.Data);
            Console.Error.WriteLine("[Hermes] " + eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            process.WaitForExit();
            string stdout;
            string stderr;
            lock (output) stdout = string.Join(Environment.NewLine, output);
            lock (errors) stderr = string.Join(Environment.NewLine, errors);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Hermes Agent 安裝超過 {InstallTimeout.TotalMinutes:0} 分鐘。");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new ProcessResult(-1, "", "Process.Start returned null.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessResult(-1, "", $"Process timed out after {timeout.TotalSeconds:0} seconds.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            return new ProcessResult(-1, "", exception.Message);
        }
    }

    private static JsonObject? ReadReceipt(string installRoot)
    {
        var path = Path.Combine(installRoot, "config", "hermes-install.json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static void WriteReceipt(string installRoot, HermesInstallationResult result, string manifestVersion)
    {
        var path = Path.Combine(installRoot, "config", "hermes-install.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var receipt = new JsonObject
        {
            ["schema_version"] = 1,
            ["source_repository"] = result.ManagedByBonsai ? "NousResearch/hermes-agent" : "existing-installation",
            ["source_commit"] = result.SourceCommit,
            ["manifest_version"] = manifestVersion,
            ["managed_by_bonsai"] = result.ManagedByBonsai,
            ["binary_path"] = result.BinaryPath,
            ["version_output"] = result.Version,
            ["updated_at_utc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        File.WriteAllText(path, receipt.ToJsonString(JsonOptions) + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }

    private static bool ReceiptMatches(JsonObject? receipt, DownloadAsset asset)
    {
        var item = receipt?[asset.Id] as JsonObject;
        return item is not null
               && item["size_bytes"]?.GetValue<long>() == asset.SizeBytes
               && string.Equals(item["sha256"]?.GetValue<string>(), asset.Sha256, StringComparison.OrdinalIgnoreCase)
               && string.Equals(item["target_relative_path"]?.GetValue<string>(), asset.TargetRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Hermes Agent path 必須是相對安裝目錄的路徑。");
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Hermes Agent 路徑逸出 Bonsai 安裝目錄。");
        return full;
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 1600 ? text[^1600..] : text;
    }

    private sealed record HermesExistingBinary(string Path, string Version);
    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

internal sealed record HermesInstallationPlan(
    string? ExistingBinaryPath,
    string Version,
    bool ManagedByBonsai,
    bool NeedsInstall,
    long EstimatedBytes,
    bool IsManagedUpdate,
    string PreviousCommit);

internal sealed record HermesInstallationResult(string BinaryPath, string Version, bool ManagedByBonsai, string SourceCommit);
