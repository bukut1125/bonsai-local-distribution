using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BonsaiSetup.Hardware;
using BonsaiSetup.Profiles;

namespace BonsaiSetup.Distribution;

internal sealed class DistributionInstaller : IDisposable
{
    private const string LauncherPayloadResource = "BonsaiSetup.launcher-payload.zip";
    private const string LauncherExecutable = "launcher\\BonsaiLauncher.exe";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ResumableDownloader _downloader = new();

    public async Task<int> InstallAsync(
        DistributionManifest manifest,
        HardwareSnapshot hardware,
        string installDirectory,
        string requestedModelId,
        string requestedProfileId,
        bool update,
        CancellationToken cancellationToken)
    {
        installDirectory = Path.GetFullPath(installDirectory);
        var root = Path.TrimEndingDirectorySeparator(installDirectory);
        EnsureRuntimeStopped(root);

        var registry = (JsonObject)manifest.ModelRegistry.DeepClone();
        var modelId = string.IsNullOrWhiteSpace(requestedModelId)
            ? RequiredString(registry, "default_model_id")
            : requestedModelId;
        var model = FindObject(RequiredArray(registry, "models"), "id", modelId);
        var hardwareSelection = SelectHardwareProfile(manifest.HardwareProfiles, hardware, registry, requestedProfileId);
        var runtimeProfileId = ResolveRuntimeProfileId(registry, modelId, requestedProfileId, hardwareSelection);
        if (update && string.IsNullOrWhiteSpace(requestedProfileId))
            runtimeProfileId = ResolvePreviousUserProfile(root, registry, hardwareSelection, modelId, runtimeProfileId);
        var runtimeProfile = FindObject(RequiredArray(registry, "profiles"), "id", runtimeProfileId);
        if (RequiredString(runtimeProfile, "model_id") != modelId)
            throw new InvalidDataException($"Runtime profile {runtimeProfileId} 不屬於 model {modelId}。");
        FilterRuntimeProfiles(registry, hardwareSelection, runtimeProfileId);
        MergeUserModelCatalog(root, registry);

        var (assets, fallbackAssets, backend) = ResolveAssets(manifest, registry, model, runtimeProfile, hardwareSelection.FallbackProfileIds);
        var spaceAssets = assets.Concat(fallbackAssets).DistinctBy(asset => asset.Id, StringComparer.Ordinal).ToArray();
        var hermesProvisioner = manifest.HermesAgent.Enabled ? new HermesProvisioner(_downloader) : null;
        var hermesPlan = hermesProvisioner is null
            ? null
            : await hermesProvisioner.InspectAsync(manifest.HermesAgent, root, update, cancellationToken).ConfigureAwait(false);
        var neededBytes = CalculateRequiredBytes(root, spaceAssets, manifest.DiskReserveBytes, hermesPlan?.EstimatedBytes ?? 0);
        while (true)
        {
            var selectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(EnsureDiskSpace(root, neededBytes)));
            if (string.Equals(selectedDirectory, root, StringComparison.OrdinalIgnoreCase)) break;
            installDirectory = selectedDirectory;
            root = selectedDirectory;
            hermesPlan = hermesProvisioner is null
                ? null
                : await hermesProvisioner.InspectAsync(manifest.HermesAgent, root, update, cancellationToken).ConfigureAwait(false);
            neededBytes = CalculateRequiredBytes(root, spaceAssets, manifest.DiskReserveBytes, hermesPlan?.EstimatedBytes ?? 0);
        }

        var previousReceipt = ReadInstalledAssetReceipt(root);
        var installLauncherPayload = !LauncherPayloadInstalled(root);
        Func<Stream>? launcherPayloadStreamFactory = null;
        var launcherPayloadBytes = installLauncherPayload ? GetLauncherPayloadBytes(out launcherPayloadStreamFactory) : 0;
        EnsureRuntimeStopped(root);

        Console.WriteLine($"硬體 profile：{hardwareSelection.ProfileId} · {hardwareSelection.DisplayName}");
        Console.WriteLine($"模型：{RequiredString(model, "display_name")}");
        Console.WriteLine($"安裝位置：{root}");
        Console.WriteLine($"預估最低可用空間：{ResumableDownloader.FormatBytes(neededBytes)}");
        foreach (var warning in hardwareSelection.Warnings) Console.WriteLine("注意：" + warning);

        Directory.CreateDirectory(root);
        WriteInstallStatus(root, "installing", manifest.Version, runtimeProfileId, update, "");
        if (installLauncherPayload) ExtractLauncherPayload(root, launcherPayloadStreamFactory!());

        var downloadedAssetPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var installedAssets = new Dictionary<string, DownloadAsset>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureAssetInstalledAsync(root, asset, previousReceipt, downloadedAssetPaths, installedAssets, cancellationToken).ConfigureAwait(false);
        }

        HermesInstallationResult? hermesInstallation = null;
        if (hermesProvisioner is not null && hermesPlan is not null)
        {
            hermesInstallation = await hermesProvisioner.EnsureInstalledAsync(
                manifest,
                root,
                hermesPlan,
                previousReceipt,
                installedAssets,
                cancellationToken).ConfigureAwait(false);
            WriteJsonFile(Path.Combine(root, "config", "installed-assets.json"), BuildAssetReceipt(installedAssets.Values));
        }

        registry["canonical_model_root"] = Path.Combine(root, "models");
        registry["default_model_id"] = modelId;
        registry["default_profile_id"] = runtimeProfileId;
        WriteJsonFile(Path.Combine(root, "config", "model-registry.json"), registry);
        WriteJsonFile(Path.Combine(root, "config", "launcher-state.json"), new JsonObject
        {
            ["schema_version"] = 1,
            ["selected_model_id"] = modelId,
            ["selected_profile_id"] = runtimeProfileId
        });

        var localRuntime = (JsonObject)manifest.LocalRuntime.DeepClone();
        var modelRoot = Path.Combine(root, "models", RequiredString(model, "storage_directory"));
        localRuntime["model_root"] = modelRoot;
        localRuntime["model_file"] = RequiredString(model, "model_file");
        localRuntime["model_id"] = modelId;
        localRuntime["expected_bytes"] = RequiredInt64(model, "expected_bytes");
        localRuntime["expected_sha256"] = RequiredString(model, "expected_sha256");
        localRuntime["candidate_root"] = "runtime\\llama-prism";
        localRuntime["default_profile"] = runtimeProfileId;
        localRuntime["endpoint"] = new JsonObject { ["host"] = "127.0.0.1", ["port"] = 18080 };
        if (hermesInstallation is not null) HermesProvisioner.ApplyToLauncherRuntime(localRuntime, hermesInstallation);
        WriteJsonFile(Path.Combine(root, "config", "local-runtime.json"), localRuntime);

        WriteJsonFile(Path.Combine(root, "config", "hardware.json"), JsonSerializer.SerializeToNode(hardware, JsonOptions)!);
        WriteJsonFile(Path.Combine(root, "config", "model.json"), AddInstallPath(model, modelRoot));
        WriteJsonFile(Path.Combine(root, "config", "runtime.json"), AddRuntimeSelection(runtimeProfile, backend, downloadedAssetPaths));
        WriteJsonFile(Path.Combine(root, "config", "model-extension.json"), manifest.ModelExtension);
        WriteJsonFile(Path.Combine(root, "config", "distribution.json"), new JsonObject
        {
            ["schema_version"] = 1,
            ["manifest_version"] = manifest.Version,
            ["manifest_channel"] = manifest.Channel,
            ["installed_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["model_id"] = modelId,
            ["profile_id"] = runtimeProfileId,
            ["runtime_revision"] = RequiredString(backend, "source_revision")
        });
        WriteJsonFile(Path.Combine(root, "config", "installed-assets.json"), BuildAssetReceipt(installedAssets.Values));
        InstallSetupCopy(root);
        if (installLauncherPayload) CreateShortcuts(root);

        var attemptedProfiles = new List<string>();
        var candidates = new[] { runtimeProfileId }.Concat(hardwareSelection.FallbackProfileIds).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        var currentProfileId = runtimeProfileId;
        string? lastError = null;
        var ready = false;
        for (var index = 0; index < candidates.Length; index++)
        {
            currentProfileId = candidates[index];
            var profileNode = FindObject(RequiredArray(registry, "profiles"), "id", currentProfileId);
            if (RequiredString(profileNode, "model_id") != modelId) continue;
            var currentBackend = FindObject(RequiredArray(registry, "backends"), "id", RequiredString(profileNode, "backend_id"));
            foreach (var assetId in GetProfileAssetIds(profileNode, currentBackend))
            {
                var asset = manifest.GetAsset(assetId);
                await EnsureAssetInstalledAsync(root, asset, previousReceipt, downloadedAssetPaths, installedAssets, cancellationToken).ConfigureAwait(false);
            }
            attemptedProfiles.Add(currentProfileId);
            WriteJsonFile(Path.Combine(root, "config", "launcher-state.json"), new JsonObject
            {
                ["schema_version"] = 1,
                ["selected_model_id"] = modelId,
                ["selected_profile_id"] = currentProfileId
            });

            Console.WriteLine($"啟動與 inference 簡測：{RequiredString(profileNode, "display_name")}");
            var start = await RunPowerShellAsync(root, "local\\start-local-model.ps1", ["-ModelId", modelId, "-ProfileId", currentProfileId, "-HealthTimeoutSeconds", "180"], TimeSpan.FromMinutes(4), cancellationToken).ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                lastError = BuildError(start);
                if (CanFallback(lastError) && index + 1 < candidates.Length)
                {
                    Console.WriteLine("啟動資源配置失敗，改用 manifest 的下一個 fallback。");
                    continue;
                }
                break;
            }

            var status = TryReadStatus(start.Stdout);
            if (status == "already_running")
            {
                lastError = "Port 18080 已由既有 runtime 持有；setup 不會停止或接管它。";
                break;
            }
            if (status != "ready")
            {
                lastError = "啟動腳本沒有回報 Ready。";
                break;
            }

            try
            {
                var probe = await RunInferenceProbeAsync(root, cancellationToken).ConfigureAwait(false);
                if (probe.Passed)
                {
                    ready = true;
                    if (hermesInstallation is not null)
                    {
                        var profileSync = await RunPowerShellAsync(root, "local\\sync-hermes-profile.ps1", [], TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
                        if (profileSync.ExitCode != 0)
                        {
                            var syncError = BuildError(profileSync);
                            WriteInstallStatus(root, "hermes_config_failed", manifest.Version, currentProfileId, update, syncError);
                            Console.Error.WriteLine("Bonsai 模型已通過推理，但 Hermes profile/MCP 設定失敗：" + syncError);
                            return 5;
                        }
                    }
                    registry["default_profile_id"] = currentProfileId;
                    WriteJsonFile(Path.Combine(root, "config", "model-registry.json"), registry);
                    localRuntime["default_profile"] = currentProfileId;
                    WriteJsonFile(Path.Combine(root, "config", "local-runtime.json"), localRuntime);
                    WriteJsonFile(Path.Combine(root, "config", "runtime.json"), AddRuntimeSelection(profileNode, currentBackend, downloadedAssetPaths));
                    WriteJsonFile(Path.Combine(root, "config", "distribution.json"), new JsonObject
                    {
                        ["schema_version"] = 1,
                        ["manifest_version"] = manifest.Version,
                        ["manifest_channel"] = manifest.Channel,
                        ["installed_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["model_id"] = modelId,
                        ["hardware_profile_id"] = hardwareSelection.ProfileId,
                        ["profile_id"] = currentProfileId,
                        ["runtime_revision"] = RequiredString(currentBackend, "source_revision")
                    });
                    WriteInstallStatus(root, "installed", manifest.Version, currentProfileId, update, "inference_passed");
                    CleanupUnreferencedPrismRuntimeFolders(root, registry);
                    CleanupUnreferencedManagedAssets(root, previousReceipt, manifest.Assets);
                    Console.WriteLine("Installation successful. API 回覆 BONSAI_OK；模型維持 Ready，可直接使用。");
                    if (!update) OpenLauncher(root);
                    else Console.WriteLine("更新完成；若控制中心仍開啟，重新開啟它以載入最新 profile 設定。");
                    return 0;
                }
                lastError = probe.Error;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                lastError = exception.Message;
            }

            if (CanFallback(lastError ?? "") && index + 1 < candidates.Length)
            {
                var stop = await RunPowerShellAsync(root, "local\\stop-local-model.ps1", [], TimeSpan.FromSeconds(40), cancellationToken).ConfigureAwait(false);
                if (stop.ExitCode != 0) throw new IOException("fallback 前停止 installer 啟動的 runtime 失敗：" + BuildError(stop));
                Console.WriteLine("Inference 資源錯誤；已停止本次啟動的 runtime，套用下一個 fallback。");
                continue;
            }

            var finalStop = await RunPowerShellAsync(root, "local\\stop-local-model.ps1", [], TimeSpan.FromSeconds(40), cancellationToken).ConfigureAwait(false);
            if (finalStop.ExitCode != 0) throw new IOException("Inference 未通過，且停止 installer 啟動的 runtime 失敗：" + BuildError(finalStop));
            break;
        }

        if (!ready)
        {
            WriteInstallStatus(root, "inference_failed", manifest.Version, currentProfileId, update, lastError ?? "No profile reached inference acceptance.");
            Console.Error.WriteLine("安裝檔案與設定已建立，但啟動/API/inference 驗收未通過；此狀態不是 Installation successful。");
            Console.Error.WriteLine("已嘗試 profiles: " + string.Join(", ", attemptedProfiles));
            Console.Error.WriteLine("原因: " + (lastError ?? "unknown"));
            return 4;
        }

        return 0;
    }

    private static long CalculateRequiredBytes(
        string root,
        IReadOnlyCollection<DownloadAsset> spaceAssets,
        long diskReserveBytes,
        long hermesInstallBytes)
    {
        var receipt = ReadInstalledAssetReceipt(root);
        var launcherMissing = !LauncherPayloadInstalled(root);
        var launcherPayloadBytes = launcherMissing ? GetLauncherPayloadBytes(out _) : 0;
        var assetsRequiringSpace = spaceAssets.Where(asset => !AssetAlreadyInstalled(root, asset, receipt)).ToArray();
        return checked(
            assetsRequiringSpace.Sum(asset => asset.SizeBytes)
            + assetsRequiringSpace.Where(asset => asset.ArchiveFormat == "zip").Sum(asset => asset.UnpackedBytes)
            + launcherPayloadBytes
            + (launcherMissing ? GetCurrentExecutableBytes() : 0)
            + hermesInstallBytes
            + diskReserveBytes);
    }

    private static ProfileSelection SelectHardwareProfile(ProfileCatalog catalog, HardwareSnapshot hardware, JsonObject registry, string requestedProfileId)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfileId))
        {
            if (catalog.Profiles.Any(profile => string.Equals(profile.Id, requestedProfileId, StringComparison.OrdinalIgnoreCase)))
                return ProfileSelector.Select(hardware, catalog, requestedProfileId);
            var runtimeProfile = RequiredArray(registry, "profiles").OfType<JsonObject>()
                .FirstOrDefault(profile => string.Equals(RequiredString(profile, "id"), requestedProfileId, StringComparison.OrdinalIgnoreCase));
            if (runtimeProfile is not null)
            {
                var automatic = ProfileSelector.Select(hardware, catalog);
                return new ProfileSelection
                {
                    ProfileId = automatic.ProfileId,
                    DisplayName = automatic.DisplayName,
                    ModelId = RequiredString(runtimeProfile, "model_id"),
                    RuntimeProfileId = requestedProfileId,
                    MemoryStrategy = automatic.MemoryStrategy,
                    ContextPreference = automatic.ContextPreference,
                    SelectedGpu = automatic.SelectedGpu,
                    IsManualOverride = true,
                    FallbackProfileIds = automatic.FallbackProfileIds,
                    AvailableProfileIds = automatic.AvailableProfileIds,
                    Warnings = automatic.Warnings
                };
            }
            throw new ArgumentException($"Profile 不存在於硬體 profile 或 launcher registry：{requestedProfileId}");
        }
        return ProfileSelector.Select(hardware, catalog);
    }

    private static string ResolveRuntimeProfileId(JsonObject registry, string modelId, string requestedProfileId, ProfileSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfileId)
            && RequiredArray(registry, "profiles").OfType<JsonObject>().Any(profile => RequiredString(profile, "id") == requestedProfileId))
            return requestedProfileId;
        if (selection.ModelId != modelId) throw new ArgumentException($"Profile 選擇的 model {selection.ModelId} 與指定 model {modelId} 不一致。");
        return selection.RuntimeProfileId;
    }

    private static string ResolvePreviousUserProfile(string root, JsonObject registry, ProfileSelection selection, string modelId, string defaultProfileId)
    {
        var distributionPath = Path.Combine(root, "config", "distribution.json");
        var launcherStatePath = Path.Combine(root, "config", "launcher-state.json");
        if (!File.Exists(distributionPath) || !File.Exists(launcherStatePath)) return defaultProfileId;
        try
        {
            var previousDistribution = JsonNode.Parse(File.ReadAllText(distributionPath))!.AsObject();
            if (previousDistribution["hardware_profile_id"]?.GetValue<string>() != selection.ProfileId) return defaultProfileId;
            var previousState = JsonNode.Parse(File.ReadAllText(launcherStatePath))!.AsObject();
            if (previousState["selected_model_id"]?.GetValue<string>() != modelId) return defaultProfileId;
            var previousProfileId = previousState["selected_profile_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(previousProfileId)) return defaultProfileId;
            if (!selection.AvailableProfileIds.Contains(previousProfileId, StringComparer.Ordinal)
                && !selection.FallbackProfileIds.Contains(previousProfileId, StringComparer.Ordinal)) return defaultProfileId;
            var profile = FindObject(RequiredArray(registry, "profiles"), "id", previousProfileId);
            return RequiredString(profile, "model_id") == modelId ? previousProfileId : defaultProfileId;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return defaultProfileId;
        }
    }

    internal static void FilterRuntimeProfiles(JsonObject registry, ProfileSelection selection, string selectedRuntimeProfileId)
    {
        var keep = selection.AvailableProfileIds
            .Concat(selection.FallbackProfileIds)
            .Append(selectedRuntimeProfileId)
            .ToHashSet(StringComparer.Ordinal);
        if (keep.Count == 0) throw new InvalidDataException($"Hardware profile {selection.ProfileId} has no selectable runtime profiles.");
        var filtered = new JsonArray();
        foreach (var profile in RequiredArray(registry, "profiles").OfType<JsonObject>())
        {
            if (keep.Contains(RequiredString(profile, "id"))) filtered.Add(profile.DeepClone());
        }
        if (!filtered.OfType<JsonObject>().Any(profile => RequiredString(profile, "id") == selectedRuntimeProfileId))
            throw new InvalidDataException($"Selected runtime profile {selectedRuntimeProfileId} was removed from the hardware profile set.");
        registry["profiles"] = filtered;
    }

    internal static void MergeUserModelCatalog(string installRoot, JsonObject registry)
    {
        var catalogPath = Path.Combine(installRoot, "config", "user-model-registry.json");
        if (!File.Exists(catalogPath)) return;

        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath)) as JsonObject
                      ?? throw new InvalidDataException("config/user-model-registry.json 無法解析。");
        MergeUserModelCatalog(catalog, registry);
    }

    internal static void MergeUserModelCatalog(JsonObject catalog, JsonObject registry)
    {
        if (catalog["schema_version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("不支援的 user-model-registry schema_version。");
        var userModels = catalog["models"] as JsonArray ?? throw new InvalidDataException("user-model-registry.models 必須是陣列。");
        var userProfiles = catalog["profiles"] as JsonArray ?? throw new InvalidDataException("user-model-registry.profiles 必須是陣列。");
        var models = RequiredArray(registry, "models");
        var backends = RequiredArray(registry, "backends");
        var profiles = RequiredArray(registry, "profiles");
        var modelIds = models.OfType<JsonObject>().Select(item => RequiredString(item, "id")).ToHashSet(StringComparer.Ordinal);
        var profileIds = profiles.OfType<JsonObject>().Select(item => RequiredString(item, "id")).ToHashSet(StringComparer.Ordinal);
        var modelById = models.OfType<JsonObject>().ToDictionary(item => RequiredString(item, "id"), StringComparer.Ordinal);
        var backendById = backends.OfType<JsonObject>().ToDictionary(item => RequiredString(item, "id"), StringComparer.Ordinal);

        foreach (var userModel in userModels.OfType<JsonObject>())
        {
            var id = RequiredString(userModel, "id");
            if (!id.StartsWith("user-", StringComparison.Ordinal)) throw new InvalidDataException($"使用者模型 id 必須以 user- 開頭：{id}");
            if (modelIds.Contains(id)) continue;
            var requiredCapabilities = userModel["required_capabilities"] as JsonArray ?? [];
            if (requiredCapabilities.Count == 0 || requiredCapabilities.Any(item => item?.GetValue<string>() != "gguf"))
                throw new InvalidDataException($"目前的本機擴展 API 只接受 GGUF capability：{id}");
            models.Add(userModel.DeepClone());
            modelIds.Add(id);
            modelById[id] = userModel;
        }

        foreach (var userProfile in userProfiles.OfType<JsonObject>())
        {
            var id = RequiredString(userProfile, "id");
            if (!id.StartsWith("user-", StringComparison.Ordinal)) throw new InvalidDataException($"使用者 profile id 必須以 user- 開頭：{id}");
            if (profileIds.Contains(id)) continue;
            var modelId = RequiredString(userProfile, "model_id");
            var backendId = RequiredString(userProfile, "backend_id");
            if (!modelById.TryGetValue(modelId, out var model)) throw new InvalidDataException($"使用者 profile {id} references unknown model {modelId}.");
            if (!backendById.TryGetValue(backendId, out var backend)) throw new InvalidDataException($"使用者 profile {id} references unknown backend {backendId}.");
            var requiredCapabilities = model["required_capabilities"] as JsonArray ?? [];
            var providedCapabilities = backend["capabilities"] as JsonArray ?? [];
            if (requiredCapabilities.Any(required => !providedCapabilities.Any(provided => string.Equals(required?.GetValue<string>(), provided?.GetValue<string>(), StringComparison.Ordinal))))
                throw new InvalidDataException($"Backend {backendId} 不符合 user model {modelId} 的 required capabilities。");
            profiles.Add(userProfile.DeepClone());
            profileIds.Add(id);
        }
    }

    private static (List<DownloadAsset> Assets, List<DownloadAsset> FallbackAssets, JsonObject Backend) ResolveAssets(
        DistributionManifest manifest,
        JsonObject registry,
        JsonObject model,
        JsonObject runtimeProfile,
        IReadOnlyList<string> fallbackProfileIds)
    {
        var assetsById = manifest.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var needed = new Dictionary<string, DownloadAsset>(StringComparer.Ordinal);
        var fallbackNeeded = new Dictionary<string, DownloadAsset>(StringComparer.Ordinal);
        AddAsset(needed, RequiredString(model, "asset_id"));
        var selectedBackend = FindObject(RequiredArray(registry, "backends"), "id", RequiredString(runtimeProfile, "backend_id"));
        AddAsset(needed, RequiredString(selectedBackend, "asset_id"));

        foreach (var adapter in OptionalArray(runtimeProfile, "adapters").OfType<JsonObject>())
        {
            AddAsset(needed, RequiredString(adapter, "asset_id"));
        }

        foreach (var fallbackId in fallbackProfileIds.Take(2))
        {
            var fallback = RequiredArray(registry, "profiles").OfType<JsonObject>().FirstOrDefault(profile => RequiredString(profile, "id") == fallbackId);
            if (fallback is null) continue;
            var fallbackBackend = FindObject(RequiredArray(registry, "backends"), "id", RequiredString(fallback, "backend_id"));
            AddAsset(fallbackNeeded, RequiredString(fallbackBackend, "asset_id"));
            foreach (var adapter in OptionalArray(fallback, "adapters").OfType<JsonObject>()) AddAsset(fallbackNeeded, RequiredString(adapter, "asset_id"));
        }

        foreach (var id in needed.Keys) fallbackNeeded.Remove(id);
        return (needed.Values.ToList(), fallbackNeeded.Values.ToList(), selectedBackend);

        void AddAsset(Dictionary<string, DownloadAsset> collection, string id)
        {
            if (!assetsById.TryGetValue(id, out var asset)) throw new InvalidDataException($"Launcher registry references missing manifest asset: {id}");
            collection[id] = asset;
        }
    }

    private async Task EnsureAssetInstalledAsync(
        string root,
        DownloadAsset asset,
        JsonObject? previousReceipt,
        Dictionary<string, string> downloadedAssetPaths,
        Dictionary<string, DownloadAsset> installedAssets,
        CancellationToken cancellationToken)
    {
        if (downloadedAssetPaths.TryGetValue(asset.Id, out var knownPath) && File.Exists(knownPath)) return;

        var finalPath = ResolveInsideRoot(root, asset.TargetRelativePath, asset.ArchiveFormat == "zip");
        if (asset.ArchiveFormat == "zip")
        {
            var executablePath = Path.Combine(finalPath, "llama-server.exe");
            if (ReceiptMatches(previousReceipt, asset) && File.Exists(executablePath))
            {
                downloadedAssetPaths[asset.Id] = executablePath;
            }
            else
            {
                var archivePath = ResolveInsideRoot(root, $"runtime\\downloads\\{asset.Id}.zip");
                await _downloader.DownloadAsync(asset, archivePath, cancellationToken).ConfigureAwait(false);
                ExtractZipSafely(archivePath, finalPath);
                File.Delete(archivePath);
                downloadedAssetPaths[asset.Id] = executablePath;
            }
        }
        else
        {
            await _downloader.DownloadAsync(asset, finalPath, cancellationToken, ReceiptMatches(previousReceipt, asset)).ConfigureAwait(false);
            downloadedAssetPaths[asset.Id] = finalPath;
        }

        installedAssets[asset.Id] = asset;
        WriteJsonFile(Path.Combine(root, "config", "installed-assets.json"), BuildAssetReceipt(installedAssets.Values));
    }

    private static IEnumerable<string> GetProfileAssetIds(JsonObject profile, JsonObject backend)
    {
        yield return RequiredString(backend, "asset_id");
        foreach (var adapter in OptionalArray(profile, "adapters").OfType<JsonObject>())
            yield return RequiredString(adapter, "asset_id");
    }

    private static long GetLauncherPayloadBytes(out Func<Stream> openStream)
    {
        var assembly = typeof(DistributionInstaller).Assembly;
        var stream = assembly.GetManifestResourceStream(LauncherPayloadResource)
                     ?? throw new InvalidDataException("安裝器未包含既有 Local Model Control Center launcher payload；請先執行 Build-Portable.ps1。");
        using (stream)
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            long bytes = 0;
            foreach (var entry in archive.Entries) bytes = checked(bytes + entry.Length);
            openStream = () => assembly.GetManifestResourceStream(LauncherPayloadResource)
                             ?? throw new InvalidDataException("無法重新讀取 launcher payload。");
            return bytes;
        }
    }

    private static bool LauncherPayloadInstalled(string root)
        => File.Exists(Path.Combine(root, LauncherExecutable))
           && File.Exists(Path.Combine(root, "local", "start-local-model.ps1"))
           && File.Exists(Path.Combine(root, "local", "stop-local-model.ps1"))
           && File.Exists(Path.Combine(root, "local", "start-hermes-local.ps1"))
           && File.Exists(Path.Combine(root, "local", "sync-hermes-profile.ps1"))
           && File.Exists(Path.Combine(root, "local", "set-reasoning-mode.ps1"))
           && File.Exists(Path.Combine(root, "local", "normalize-local-reasoning.ps1"))
           && File.Exists(Path.Combine(root, "local", "test-hermes-local.ps1"))
           && File.Exists(Path.Combine(root, "config", "hermes-local-template.yaml"))
           && File.Exists(Path.Combine(root, "hermes", "reasoning", "orca-reasoning-policy.json"))
           && File.Exists(Path.Combine(root, "hermes", "reasoning", "model-providers", "orca-local", "__init__.py"))
           && File.Exists(Path.Combine(root, "hermes", "reasoning", "plugins", "orca-reasoning-scheduler", "__init__.py"));

    private static bool AssetAlreadyInstalled(string root, DownloadAsset asset, JsonObject? receipt)
    {
        if (!ReceiptMatches(receipt, asset)) return false;
        var target = Path.GetFullPath(Path.Combine(root, asset.TargetRelativePath));
        EnsurePathInsideRoot(root, target);
        if (asset.ArchiveFormat == "zip") return File.Exists(Path.Combine(target, "llama-server.exe"));
        return File.Exists(target) && new FileInfo(target).Length == asset.SizeBytes;
    }

    private static long GetCurrentExecutableBytes()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        return new FileInfo(path).Length;
    }

    private static string EnsureDiskSpace(string installDirectory, long requiredBytes)
    {
        while (true)
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(installDirectory)) ?? throw new IOException("無法判定安裝磁碟。");
            var drive = DriveInfo.GetDrives().FirstOrDefault(item => string.Equals(item.Name, driveRoot, StringComparison.OrdinalIgnoreCase));
            if (drive is not null && drive.IsReady && drive.AvailableFreeSpace >= requiredBytes) return installDirectory;
            var free = drive is { IsReady: true } ? ResumableDownloader.FormatBytes(drive.AvailableFreeSpace) : "無法讀取";
            Console.WriteLine($"安裝位置可用空間不足：需要約 {ResumableDownloader.FormatBytes(requiredBytes)}，目前 {free}。輸入另一個資料夾路徑，或直接 Enter 結束。");
            if (Console.IsInputRedirected) throw new IOException("安裝磁碟空間不足，且目前沒有互動式輸入可選擇其他磁碟。");
            var alternate = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(alternate)) throw new IOException("使用者取消磁碟選擇。");
            installDirectory = Path.GetFullPath(alternate.Trim().Trim('"'));
        }
    }

    private static void ExtractLauncherPayload(string root, Stream payloadStream)
    {
        using (payloadStream)
        using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                EnsurePathInsideRoot(root, destination);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                var relativeDestination = Path.GetRelativePath(root, destination).Replace('\\', '/');
                if (string.Equals(relativeDestination, "launcher/BonsaiLauncher.exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
    }

    private static void ExtractZipSafely(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            EnsurePathInsideRoot(root, destination);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string ResolveInsideRoot(string root, string relativePath, bool directory = false)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("manifest asset path 必須是相對路徑。");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsurePathInsideRoot(root, fullPath);
        if (directory) Directory.CreateDirectory(fullPath);
        else Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static void EnsurePathInsideRoot(string root, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("封裝路徑逸出 Bonsai 安裝目錄。");
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string root, string relativeScript, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var scriptPath = ResolveInsideRoot(root, relativeScript);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("launcher runtime script 不存在。", scriptPath);
        var executable = ResolvePowerShell();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root,
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
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("無法啟動內建 Windows PowerShell。");
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
            throw new TimeoutException($"PowerShell operation exceeded {timeout.TotalSeconds:N0} seconds.");
        }
    }

    private static string ResolvePowerShell()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var candidates = new[]
        {
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"),
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            systemPowerShell
        };
        return candidates.FirstOrDefault(File.Exists) ?? "powershell.exe";
    }

    private static async Task<(bool Passed, string Error)> RunInferenceProbeAsync(string root, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var baseUrl = "http://127.0.0.1:18080";
        using var health = await http.GetAsync(baseUrl + "/health", cancellationToken).ConfigureAwait(false);
        if (!health.IsSuccessStatusCode) return (false, $"/health returned HTTP {(int)health.StatusCode}");
        using var modelsResponse = await http.GetAsync(baseUrl + "/v1/models", cancellationToken).ConfigureAwait(false);
        if (!modelsResponse.IsSuccessStatusCode) return (false, $"/v1/models returned HTTP {(int)modelsResponse.StatusCode}");

        var registry = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "model-registry.json"), cancellationToken).ConfigureAwait(false))!.AsObject();
        var selectedProfileId = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "launcher-state.json"), cancellationToken).ConfigureAwait(false))!["selected_profile_id"]!.GetValue<string>();
        var profile = FindObject(RequiredArray(registry, "profiles"), "id", selectedProfileId);
        var modelAlias = RequiredString(profile, "model_alias");

        using var modelDocument = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var aliases = modelDocument.RootElement.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        if (!aliases.Contains(modelAlias, StringComparer.Ordinal)) return (false, $"/v1/models 沒有 profile alias {modelAlias}");

        var payload = JsonSerializer.Serialize(new
        {
            model = modelAlias,
            messages = new[] { new { role = "user", content = "Reply exactly: BONSAI_OK" } },
            max_tokens = 16,
            temperature = 0
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(baseUrl + "/v1/chat/completions", content, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (false, $"chat completion returned HTTP {(int)response.StatusCode}: {responseText}");

        using var responseDocument = JsonDocument.Parse(responseText);
        var result = responseDocument.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        return result == "BONSAI_OK" ? (true, "") : (false, $"chat completion did not return exact BONSAI_OK: {result}");
    }

    private static bool CanFallback(string message)
    {
        var text = message.ToLowerInvariant();
        return text.Contains("out of memory", StringComparison.Ordinal)
               || text.Contains("cudaerroroutofmemory", StringComparison.Ordinal)
               || text.Contains("cuda error", StringComparison.Ordinal)
               || text.Contains("cublas", StringComparison.Ordinal)
               || text.Contains("cudart", StringComparison.Ordinal)
               || text.Contains("nvidia-smi", StringComparison.Ordinal)
               || text.Contains("gpu memory is below", StringComparison.Ordinal)
               || text.Contains("failed to load", StringComparison.Ordinal)
               || text.Contains("allocation", StringComparison.Ordinal);
    }

    private static string TryReadStatus(string stdout)
    {
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("status", out var status)) return status.GetString() ?? "";
            }
            catch (JsonException) { }
        }
        return "";
    }

    private static string BuildError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 1200 ? text[..1200] : text;
    }

    private static void EnsureRuntimeStopped(string root)
    {
        var activePath = Path.Combine(root, "runtime", "llama-local", "active-local-runtime.json");
        if (!File.Exists(activePath)) return;
        try
        {
            var active = JsonNode.Parse(File.ReadAllText(activePath))!.AsObject();
            if (RequiredString(active, "status") == "ready")
                throw new InvalidOperationException("Bonsai runtime 目前標記為 Ready。請先在既有 launcher 停止模型，再執行更新；setup 不會停止使用者程序。");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException("無法確認已安裝 runtime 狀態，為避免覆寫使用中的檔案，停止更新。", exception);
        }
    }

    private static void WriteInstallStatus(string root, string status, string version, string profileId, bool update, string message)
    {
        WriteJsonFile(Path.Combine(root, "config", "install-state.json"), new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = status,
            ["manifest_version"] = version,
            ["profile_id"] = profileId,
            ["operation"] = update ? "update" : "install",
            ["message"] = message,
            ["updated_at_utc"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private static JsonObject BuildAssetReceipt(IEnumerable<DownloadAsset> assets)
    {
        var receipt = new JsonObject { ["schema_version"] = 1 };
        foreach (var asset in assets)
        {
            receipt[asset.Id] = new JsonObject
            {
                ["kind"] = asset.Kind,
                ["size_bytes"] = asset.SizeBytes,
                ["sha256"] = asset.Sha256,
                ["revision"] = asset.Revision,
                ["target_relative_path"] = asset.TargetRelativePath
            };
        }
        return receipt;
    }

    private static JsonObject AddInstallPath(JsonObject model, string modelRoot)
    {
        var result = (JsonObject)model.DeepClone();
        result["absolute_model_path"] = Path.Combine(modelRoot, RequiredString(model, "model_file"));
        return result;
    }

    private static JsonObject AddRuntimeSelection(JsonObject profile, JsonObject backend, IReadOnlyDictionary<string, string> assetPaths)
    {
        var result = (JsonObject)profile.DeepClone();
        result["backend_display_name"] = RequiredString(backend, "display_name");
        result["executable_relative_path"] = RequiredString(backend, "executable_relative_path");
        result["asset_id"] = RequiredString(backend, "asset_id");
        result["backend_executable_ready"] = assetPaths.ContainsKey(RequiredString(backend, "asset_id"));
        return result;
    }

    private static JsonObject? ReadInstalledAssetReceipt(string root)
    {
        var path = Path.Combine(root, "config", "installed-assets.json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static bool ReceiptMatches(JsonObject? receipt, DownloadAsset asset)
    {
        var item = receipt?[asset.Id] as JsonObject;
        return item is not null
               && item["size_bytes"]?.GetValue<long>() == asset.SizeBytes
               && string.Equals(item["sha256"]?.GetValue<string>(), asset.Sha256, StringComparison.OrdinalIgnoreCase)
               && string.Equals(item["target_relative_path"]?.GetValue<string>(), asset.TargetRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupUnreferencedPrismRuntimeFolders(string root, JsonObject registry)
    {
        var runtimeRoot = Path.GetFullPath(Path.Combine(root, "runtime", "llama-prism"));
        if (!Directory.Exists(runtimeRoot)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in RequiredArray(registry, "backends").OfType<JsonObject>())
        {
            var executable = Path.GetFullPath(Path.Combine(root, RequiredString(backend, "executable_relative_path")));
            EnsurePathInsideRoot(runtimeRoot, executable);
            referenced.Add(Path.GetDirectoryName(executable)!);
        }

        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            var fullPath = Path.GetFullPath(directory);
            EnsurePathInsideRoot(runtimeRoot, fullPath);
            if (referenced.Contains(fullPath)) continue;
            var info = new DirectoryInfo(fullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (!info.Name.StartsWith("prism-", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static void CleanupUnreferencedManagedAssets(string root, JsonObject? previousReceipt, IReadOnlyList<DownloadAsset> currentAssets)
    {
        if (previousReceipt is null) return;
        var currentPaths = currentAssets
            .Where(asset => asset.Kind is "model" or "adapter")
            .ToDictionary(asset => asset.Id, asset => asset.TargetRelativePath, StringComparer.Ordinal);
        var modelRootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "models"))) + Path.DirectorySeparatorChar;

        foreach (var entry in previousReceipt)
        {
            if (entry.Value is not JsonObject receipt || receipt["kind"]?.GetValue<string>() is not ("model" or "adapter")) continue;
            var assetId = entry.Key;
            var relativePath = receipt["target_relative_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            if (currentPaths.TryGetValue(assetId, out var currentPath) && string.Equals(currentPath, relativePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.IsPathRooted(relativePath)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsurePathInsideRoot(root, fullPath);
            if (!fullPath.StartsWith(modelRootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(fullPath);
        }
    }

    private static void InstallSetupCopy(string root)
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || !string.Equals(Path.GetFileName(source), "BonsaiSetup.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目前執行檔不是可發行的 BonsaiSetup.exe；開發環境不能建立 updater 副本。");
        var destination = Path.Combine(root, "updater", "BonsaiSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, overwrite: true);
    }

    private static void CreateShortcuts(string root)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Bonsai Local");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, "Bonsai Launcher.lnk"), Path.Combine(root, LauncherExecutable), root, "");
            CreateShortcut(Path.Combine(desktop, "Bonsai Launcher.lnk"), Path.Combine(root, LauncherExecutable), root, "");
            var updater = Path.Combine(root, "updater", "BonsaiSetup.exe");
            var updateArguments = $"--update --install-dir \"{root}\"";
            CreateShortcut(Path.Combine(startMenu, "Bonsai Update.lnk"), updater, root, updateArguments);
            CreateShortcut(Path.Combine(desktop, "Bonsai Update.lnk"), updater, root, updateArguments);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("捷徑未建立：" + exception.Message);
        }
    }

    private static void CreateShortcut(string linkPath, string target, string workingDirectory, string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host shortcut COM unavailable.");
        dynamic shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Unable to create WScript.Shell.");
        dynamic shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Arguments = arguments;
        shortcut.Save();
    }

    private static void OpenLauncher(string root)
    {
        var launcher = Path.Combine(root, LauncherExecutable);
        if (!File.Exists(launcher)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = launcher, WorkingDirectory = root, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("模型已 Ready，但控制中心未自動開啟：" + exception.Message);
        }
    }

    private static void WriteJsonFile(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, value.ToJsonString(JsonOptions) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonObject RequiredObject(JsonObject source, string property) => source[property] as JsonObject
        ?? throw new InvalidDataException($"manifest 缺少 object: {property}");

    private static JsonArray RequiredArray(JsonObject source, string property) => source[property] as JsonArray
        ?? throw new InvalidDataException($"manifest 缺少 array: {property}");

    private static JsonArray OptionalArray(JsonObject source, string property) => source[property] as JsonArray ?? [];

    private static JsonObject FindObject(JsonArray items, string key, string value) => items.OfType<JsonObject>()
        .SingleOrDefault(item => string.Equals(item[key]?.GetValue<string>(), value, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"launcher registry 找不到 {key}={value}");

    private static string RequiredString(JsonObject source, string property) => source[property]?.GetValue<string>()
        ?? throw new InvalidDataException($"launcher registry 欄位缺少字串: {property}");

    private static long RequiredInt64(JsonObject source, string property) => source[property]?.GetValue<long>()
        ?? throw new InvalidDataException($"launcher registry 欄位缺少數值: {property}");

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    public void Dispose() => _downloader.Dispose();
}
