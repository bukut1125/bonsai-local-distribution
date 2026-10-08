using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http.Headers;
using BonsaiSetup.Agent;
using BonsaiSetup.Distribution;
using BonsaiSetup.Hardware;
using BonsaiSetup.Profiles;

namespace BonsaiSetup;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.Help)
            {
                PrintHelp();
                return 0;
            }

            if (options.TestSelector)
            {
                return ProfileSelectorTests.Run();
            }

            if (options.TestManifest)
            {
                return ValidateLocalManifest();
            }

            if (options.TestDownloader)
            {
                return await ValidateResumableDownloaderAsync().ConfigureAwait(false);
            }

            if (options.TestHermes)
            {
                return await ValidateHermesProvisioningAsync().ConfigureAwait(false);
            }

            if (options.McpServer || options.AgentTask is not null)
            {
                if (string.IsNullOrWhiteSpace(options.InstallDirectory))
                    throw new ArgumentException("--mcp 與 --agent-task 必須搭配 --install-dir <BonsaiLocal>。");
                var root = Path.GetFullPath(options.InstallDirectory);
                if (options.McpServer) return await new McpServer(root).RunAsync(CancellationToken.None).ConfigureAwait(false);
                return await BonsaiAgentTaskRunner.RunAsync(root, options.AgentTask!, CancellationToken.None).ConfigureAwait(false);
            }

            if (options.Diagnose && options.Update)
                throw new ArgumentException("--diagnose 不可與 --update 混用。");

            var settings = BootstrapSettings.Load();
            var installDirectory = ResolveInstallDirectory(options, settings);
            var hardware = WindowsHardwareDetector.Detect(installDirectory, options.ForceDxgi);

            if (options.Diagnose)
            {
                var catalog = ProfileSelector.LoadEmbedded();
                var selection = ProfileSelector.Select(hardware, catalog, options.Profile);
                var report = new DiagnosticReport
                {
                    Hardware = hardware,
                    Selection = selection,
                    ModelId = options.Model ?? catalog.ModelId,
                    InstallDirectory = installDirectory,
                    Stage = "hardware-and-profile-selection"
                };

                if (options.OutputPath is not null)
                {
                    var outputPath = Path.GetFullPath(options.OutputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                    Console.WriteLine($"診斷資料已寫入：{outputPath}");
                }

                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return 0;
            }

            if (options.ForceDxgi) throw new ArgumentException("--test-dxgi 只能與 --diagnose 一起使用。");
            if (options.OutputPath is not null) throw new ArgumentException("--output 只能與 --diagnose 一起使用。");
            if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
                throw new InvalidOperationException("尚未設定 GitHub manifest URL。請在發行前將 bootstrap.json 的 manifest_url 指向 bonsai-local-distribution 的 stable.json。");

            Console.WriteLine("掃描完成；正在讀取 GitHub stable manifest。");
            using var manifestClient = new ManifestClient();
            var manifest = await manifestClient.FetchAsync(settings.ManifestUrl, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"載入 manifest {manifest.Version} ({manifest.Channel})。");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            using var installer = new DistributionInstaller();
            try
            {
                return await installer.InstallAsync(
                    manifest,
                    hardware,
                    installDirectory,
                    options.Model ?? "",
                    options.Profile ?? "",
                    options.Update,
                    cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or TimeoutException or OperationCanceledException or JsonException)
        {
            Console.Error.WriteLine("BonsaiSetup: " + exception.Message);
            return 1;
        }
    }

    private static string ResolveInstallDirectory(CommandLineOptions options, BootstrapSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(options.InstallDirectory)) return Path.GetFullPath(options.InstallDirectory);
        if (options.Portable) return Path.Combine(AppContext.BaseDirectory, "BonsaiLocal");
        var configured = Environment.ExpandEnvironmentVariables(settings.DefaultInstallDirectory);
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains('%'))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("Windows 沒有提供 LOCALAPPDATA。");
            configured = Path.Combine(localAppData, "BonsaiLocal");
        }
        return Path.GetFullPath(configured);
    }

    private static int ValidateLocalManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var manifestPath = Path.Combine(directory.FullName, "manifests", "stable.json");
            var profilesPath = Path.Combine(directory.FullName, "profiles", "hardware-profiles.json");
            if (File.Exists(manifestPath) && File.Exists(profilesPath))
            {
                var manifest = DistributionManifest.Parse(File.ReadAllText(manifestPath), File.ReadAllText(profilesPath));
                var models = manifest.ModelRegistry["models"]!.AsArray().Count;
                var backends = manifest.ModelRegistry["backends"]!.AsArray().Count;
                var profiles = manifest.ModelRegistry["profiles"]!.AsArray().Count;
                var fixture = new HardwareSnapshot
                {
                    Gpus = [new GpuInformation { Name = "Selector fixture", Vendor = "NVIDIA", VendorId = "10DE", DedicatedMemoryMiB = 7960, Source = "fixture" }],
                    Memory = new MemoryInformation { TotalBytes = 32_000_000_000, AvailableBytes = 16_000_000_000 }
                };
                var eightGbSelection = ProfileSelector.Select(fixture, manifest.HardwareProfiles);
                var filteredRegistry = (JsonObject)manifest.ModelRegistry.DeepClone();
                DistributionInstaller.FilterRuntimeProfiles(filteredRegistry, eightGbSelection, eightGbSelection.RuntimeProfileId);
                var filteredProfiles = filteredRegistry["profiles"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
                if (filteredProfiles.Length > 6 || filteredProfiles.Any(id => id.Contains("12gb-long", StringComparison.Ordinal) || id.Contains("16gb-max", StringComparison.Ordinal)))
                    throw new InvalidDataException("8 GB launcher registry exposes profiles from incompatible hardware classes.");
                var selectedProfile = manifest.ModelRegistry["profiles"]!.AsArray().OfType<JsonObject>()
                    .Single(profile => profile["id"]?.GetValue<string>() == eightGbSelection.RuntimeProfileId);
                var selectedModel = manifest.ModelRegistry["models"]!.AsArray().OfType<JsonObject>()
                    .Single(model => model["id"]?.GetValue<string>() == selectedProfile["model_id"]?.GetValue<string>());
                var assetPlan = DistributionInstaller.ResolveAssets(
                    manifest, manifest.ModelRegistry, selectedModel, selectedProfile, eightGbSelection.FallbackProfileIds);
                var selectedAssetIds = assetPlan.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
                if (!selectedAssetIds.Contains("prism_cuda_runtime"))
                    throw new InvalidDataException("8 GB CUDA profile did not select the required Prism CUDA runtime DLL asset.");
                if (assetPlan.FallbackAssets.Any(asset => asset.Id == "prism_cuda_runtime"))
                    throw new InvalidDataException("CUDA runtime DLL asset should be shared with the selected 8 GB backend, not downloaded twice for fallback.");
                var userCatalogFixture = new JsonObject
                {
                    ["schema_version"] = 1,
                    ["models"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "user-fixture-model",
                        ["display_name"] = "Fixture user GGUF",
                        ["required_capabilities"] = new JsonArray("gguf")
                    }),
                    ["profiles"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "user-fixture-profile",
                        ["display_name"] = "Fixture user profile",
                        ["model_id"] = "user-fixture-model",
                        ["backend_id"] = "llama-prism-b10709-cuda"
                    })
                };
                DistributionInstaller.MergeUserModelCatalog(userCatalogFixture, filteredRegistry);
                var mergedModelIds = filteredRegistry["models"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
                var mergedProfileIds = filteredRegistry["profiles"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
                if (!mergedModelIds.Contains("user-fixture-model", StringComparer.Ordinal) || !mergedProfileIds.Contains("user-fixture-profile", StringComparer.Ordinal))
                    throw new InvalidDataException("User model catalog entries were not preserved after hardware-profile filtering.");
                Console.WriteLine($"PASS · manifest {manifest.Version} · {manifest.Assets.Count} assets · {models} models · {backends} backends · {profiles} runtime profiles · {manifest.HardwareProfiles.Profiles.Count} hardware profiles · 8 GB menu={filteredProfiles.Length} profiles · CUDA runtime dependency selected · user model/profile retained through update merge");
                return 0;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("開發階段找不到 manifests/stable.json 與 profiles/hardware-profiles.json。");
    }

    private static async Task<int> ValidateHermesProvisioningAsync()
    {
        var projectRoot = FindDistributionProjectRoot();
        var manifest = DistributionManifest.Parse(
            File.ReadAllText(Path.Combine(projectRoot, "manifests", "stable.json")),
            File.ReadAllText(Path.Combine(projectRoot, "profiles", "hardware-profiles.json")));
        if (!manifest.HermesAgent.Enabled) throw new InvalidDataException("Stable manifest does not enable Hermes Agent.");

        var asset = manifest.GetAsset(manifest.HermesAgent.BootstrapAssetId);
        var temp = Path.Combine(Path.GetTempPath(), "BonsaiHermesInstallerProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var downloaded = Path.Combine(temp, "install.ps1");
        try
        {
            using var downloader = new ResumableDownloader();
            await downloader.DownloadAsync(asset, downloaded, CancellationToken.None).ConfigureAwait(false);
            var source = await File.ReadAllTextAsync(downloaded).ConfigureAwait(false);
            var pathFree = HermesProvisioner.CreatePathFreeInstallerScript(source);
            var userPathWriteCount = System.Text.RegularExpressions.Regex.Matches(source, @"\[Environment\]::SetEnvironmentVariable\(\s*['""]Path['""]").Count;
            if (userPathWriteCount != 1) throw new InvalidDataException("Pinned Hermes installer changed its user PATH write surface.");
            if (System.Text.RegularExpressions.Regex.IsMatch(pathFree, @"(?m)^[ \t]*Set-LauncherUserPath[ \t]+\$binDir[ \t]*$"))
                throw new InvalidDataException("Hermes installer still contains an active user PATH write call.");
            if (System.Text.RegularExpressions.Regex.IsMatch(source, @"(?im)^\s*Start-Process.*-Verb\s+RunAs"))
                throw new InvalidDataException("Pinned Hermes installer unexpectedly requests elevation.");
            if (!pathFree.Contains("Bonsai: keep PATH unchanged; the launcher calls hermes.exe by absolute path.", StringComparison.Ordinal))
                throw new InvalidDataException("Hermes installer PATH-free patch marker is missing.");

            Console.WriteLine($"PASS · pinned Hermes installer {manifest.HermesAgent.SourceCommit} SHA-256 verified · no admin elevation · user PATH hook disabled · no installer executed");
            return 0;
        }
        finally
        {
            if (File.Exists(downloaded)) File.Delete(downloaded);
            var partial = downloaded + ".partial";
            if (File.Exists(partial)) File.Delete(partial);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: false);
        }
    }

    private static async Task<int> ValidateResumableDownloaderAsync()
    {
        var projectRoot = FindDistributionProjectRoot();
        var manifest = DistributionManifest.Parse(
            File.ReadAllText(Path.Combine(projectRoot, "manifests", "stable.json")),
            File.ReadAllText(Path.Combine(projectRoot, "profiles", "hardware-profiles.json")));
        var asset = manifest.GetAsset("prism_cpu");
        var staging = Path.Combine(projectRoot, "staging");
        Directory.CreateDirectory(staging);
        var destination = Path.Combine(staging, "prism-cpu-resume-check.zip");
        var partial = destination + ".partial";
        using var downloader = new ResumableDownloader();
        if (File.Exists(destination))
        {
            await downloader.DownloadAsync(asset, destination, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"PASS · cached CPU runtime asset size/SHA-256 verified · {asset.SizeBytes:N0} bytes");
            return 0;
        }
        if (!File.Exists(partial))
        {
            var seededBytes = await SeedRangePartialAsync(asset, partial, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"測試伺服器先傳回 {seededBytes:N0} bytes；下載器將從此 partial 續傳。");
        }
        var resumedFromBytes = new FileInfo(partial).Length;
        if (resumedFromBytes <= 0 || resumedFromBytes >= asset.SizeBytes) throw new InvalidDataException("Range 測試 partial 長度必須介於 0 與完整資產大小之間。");

        await downloader.DownloadAsync(asset, destination, CancellationToken.None).ConfigureAwait(false);
        if (new FileInfo(destination).Length != asset.SizeBytes) throw new InvalidDataException("Resumed CPU runtime size differs from manifest.");
        Console.WriteLine($"PASS · HTTP Range resume from {resumedFromBytes:N0} bytes · expected size {asset.SizeBytes:N0} · SHA-256 {asset.Sha256}");
        return 0;
    }

    private static async Task<long> SeedRangePartialAsync(DownloadAsset asset, string path, CancellationToken cancellationToken)
    {
        var rangeEnd = Math.Min(asset.SizeBytes - 1, 1_048_575);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Range = new RangeHeaderValue(0, rangeEnd);
        request.Headers.UserAgent.ParseAdd("BonsaiLocalDistribution-test/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != 0)
            throw new HttpRequestException("Pinned CPU runtime source did not honor the test Range request.");
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException("CPU runtime test source redirected to a non-HTTPS URL.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        var bytes = target.Length;
        if (bytes <= 0 || bytes >= asset.SizeBytes) throw new InvalidDataException("The Range test prefix was not partial.");
        return bytes;
    }

    private static string FindDistributionProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bootstrap.json"))
                && File.Exists(Path.Combine(directory.FullName, "manifests", "stable.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("開發階段找不到 Bonsai Local Distribution 專案根目錄。");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("BonsaiSetup · Windows 本地模型 bootstrapper");
        Console.WriteLine("  --diagnose                 掃描硬體並輸出 profile 建議，不啟動模型");
        Console.WriteLine("  --output <path>            將診斷結果另存為 UTF-8 JSON");
        Console.WriteLine("  --profile <id>             覆寫硬體 profile 或 runtime profile");
        Console.WriteLine("  --model <id>               指定 manifest 內的模型 id");
        Console.WriteLine("  --install-dir <path>       指定安裝路徑");
        Console.WriteLine("  --portable                 預設安裝至執行檔旁的 BonsaiLocal 資料夾");
        Console.WriteLine("  --update                   更新已安裝的 runtime/model/config");
        Console.WriteLine("  --agent-task <text>        讓目前 Bonsai 模型透過受限模型工具接入一個 Hugging Face GGUF");
        Console.WriteLine("  --mcp                      以 MCP stdio 介面提供本機模型接入工具");
        Console.WriteLine("  --test-selector            執行小型硬體 profile 正反例");
        Console.WriteLine("  --test-manifest            驗證本機 stable manifest 的來源關係");
        Console.WriteLine("  --test-downloader          使用小型 CPU runtime 部分檔驗證 HTTP Range 續傳與 SHA");
        Console.WriteLine("  --test-hermes              驗證 Hermes 固定來源雜湊與免 PATH patch；不執行安裝");
        Console.WriteLine("  --test-dxgi                強制走 DXGI GPU fallback 作唯讀探測");
    }
}

internal sealed class CommandLineOptions
{
    public bool Help { get; private set; }
    public bool Diagnose { get; private set; }
    public bool Portable { get; private set; }
    public bool TestSelector { get; private set; }
    public bool TestManifest { get; private set; }
    public bool TestDownloader { get; private set; }
    public bool TestHermes { get; private set; }
    public bool ForceDxgi { get; private set; }
    public bool Update { get; private set; }
    public bool McpServer { get; private set; }
    public string? InstallDirectory { get; private set; }
    public string? Profile { get; private set; }
    public string? Model { get; private set; }
    public string? AgentTask { get; private set; }
    public string? OutputPath { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var parsed = new CommandLineOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                    parsed.Help = true;
                    break;
                case "--diagnose":
                    parsed.Diagnose = true;
                    break;
                case "--portable":
                    parsed.Portable = true;
                    break;
                case "--update":
                    parsed.Update = true;
                    break;
                case "--mcp":
                    parsed.McpServer = true;
                    break;
                case "--agent-task":
                    parsed.AgentTask = ReadValue(args, ref index, arg);
                    break;
                case "--test-selector":
                    parsed.TestSelector = true;
                    break;
                case "--test-manifest":
                    parsed.TestManifest = true;
                    break;
                case "--test-downloader":
                    parsed.TestDownloader = true;
                    break;
                case "--test-hermes":
                    parsed.TestHermes = true;
                    break;
                case "--test-dxgi":
                    parsed.ForceDxgi = true;
                    parsed.Diagnose = true;
                    break;
                case "--install-dir":
                    parsed.InstallDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--profile":
                    parsed.Profile = ReadValue(args, ref index, arg);
                    break;
                case "--model":
                    parsed.Model = ReadValue(args, ref index, arg);
                    break;
                case "--output":
                    parsed.OutputPath = ReadValue(args, ref index, arg);
                    break;
                default:
                    throw new ArgumentException($"未知參數：{arg}");
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw new ArgumentException($"{option} 缺少值。");
        return args[index];
    }
}

internal sealed class DiagnosticReport
{
    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "";
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";
    [JsonPropertyName("install_directory")]
    public string InstallDirectory { get; init; } = "";
    [JsonPropertyName("collected_at_utc")]
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("hardware")]
    public HardwareSnapshot Hardware { get; init; } = new();
    [JsonPropertyName("selection")]
    public ProfileSelection Selection { get; init; } = new();
}
