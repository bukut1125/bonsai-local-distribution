using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BonsaiSetup.Profiles;

namespace BonsaiSetup.Distribution;

internal sealed class DistributionManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("channel")] public string Channel { get; init; } = "stable";
    [JsonPropertyName("disk_reserve_bytes")] public long DiskReserveBytes { get; init; }
    [JsonPropertyName("assets")] public List<DownloadAsset> Assets { get; init; } = [];
    [JsonPropertyName("hardware_profiles_url")] public string HardwareProfilesUrl { get; init; } = "";
    [JsonIgnore] public ProfileCatalog HardwareProfiles { get; private set; } = new();
    [JsonPropertyName("model_registry")] public JsonObject ModelRegistry { get; init; } = new();
    [JsonPropertyName("local_runtime")] public JsonObject LocalRuntime { get; init; } = new();

    public static DistributionManifest Parse(string json, string hardwareProfilesJson)
    {
        var manifest = JsonSerializer.Deserialize<DistributionManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("GitHub manifest 為空或 JSON 無法解析。");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"不支援的 GitHub manifest schema: {manifest.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("GitHub manifest 缺少 version。");
        if (manifest.DiskReserveBytes < 0) throw new InvalidDataException("GitHub manifest disk_reserve_bytes 不可小於零。");
        if (manifest.Assets.Count == 0) throw new InvalidDataException("GitHub manifest 沒有下載資產。");
        manifest.HardwareProfiles = JsonSerializer.Deserialize<ProfileCatalog>(hardwareProfilesJson, JsonOptions)
                                   ?? throw new InvalidDataException("GitHub hardware profile JSON 為空或無法解析。");
        if (manifest.HardwareProfiles.SchemaVersion != 1 || manifest.HardwareProfiles.Profiles.Count == 0) throw new InvalidDataException("GitHub manifest hardware_profiles 無效。");
        if (manifest.ModelRegistry.Count == 0 || manifest.LocalRuntime.Count == 0) throw new InvalidDataException("GitHub manifest 缺少 launcher 配置。");
        if (!manifest.ModelRegistry.TryGetPropertyValue("profiles", out var profiles) || profiles is not JsonArray { Count: > 0 })
            throw new InvalidDataException("GitHub manifest model_registry.profiles 不可為空。");
        if (!manifest.ModelRegistry.TryGetPropertyValue("endpoint", out var endpoint) || endpoint?["host"]?.GetValue<string>() != "127.0.0.1")
            throw new InvalidDataException("GitHub manifest API 必須限制在 127.0.0.1。");
        var port = endpoint?["port"]?.GetValue<int>() ?? 0;
        if (port != 18080) throw new InvalidDataException("GitHub manifest API port 必須符合既有 launcher 的 127.0.0.1:18080 契約。");
        if (manifest.Assets.Select(asset => asset.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Assets.Count)
            throw new InvalidDataException("GitHub manifest asset id 重複。");
        foreach (var asset in manifest.Assets) asset.Validate();
        manifest.ValidateReferences();
        return manifest;
    }

    private void ValidateReferences()
    {
        var assets = Assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        var models = ModelRegistry["models"] as JsonArray ?? throw new InvalidDataException("model_registry.models 不可為空。");
        var backends = ModelRegistry["backends"] as JsonArray ?? throw new InvalidDataException("model_registry.backends 不可為空。");
        var profiles = ModelRegistry["profiles"] as JsonArray ?? throw new InvalidDataException("model_registry.profiles 不可為空。");
        var modelIds = models.OfType<JsonObject>().Select(item => ReadString(item, "id")).ToHashSet(StringComparer.Ordinal);
        var backendById = backends.OfType<JsonObject>().ToDictionary(item => ReadString(item, "id"), StringComparer.Ordinal);
        var profileById = profiles.OfType<JsonObject>().ToDictionary(item => ReadString(item, "id"), StringComparer.Ordinal);
        if (modelIds.Count == 0 || modelIds.Count != models.Count) throw new InvalidDataException("model_registry model id 為空或重複。");
        if (backendById.Count == 0 || backendById.Count != backends.Count) throw new InvalidDataException("model_registry backend id 為空或重複。");
        if (profileById.Count == 0 || profileById.Count != profiles.Count) throw new InvalidDataException("model_registry profile id 為空或重複。");
        if (!modelIds.Contains(ReadString(ModelRegistry, "default_model_id"))) throw new InvalidDataException("model_registry.default_model_id is unknown.");
        if (!profileById.ContainsKey(ReadString(ModelRegistry, "default_profile_id"))) throw new InvalidDataException("model_registry.default_profile_id is unknown.");

        foreach (var model in models.OfType<JsonObject>())
        {
            if (!assets.Contains(ReadString(model, "asset_id"))) throw new InvalidDataException($"Model {ReadString(model, "id")} references an unknown asset.");
        }
        foreach (var backend in backends.OfType<JsonObject>())
        {
            if (!assets.Contains(ReadString(backend, "asset_id"))) throw new InvalidDataException($"Backend {ReadString(backend, "id")} references an unknown asset.");
        }
        foreach (var profile in profiles.OfType<JsonObject>())
        {
            if (!modelIds.Contains(ReadString(profile, "model_id"))) throw new InvalidDataException($"Profile {ReadString(profile, "id")} references an unknown model.");
            var backendId = ReadString(profile, "backend_id");
            if (!backendById.TryGetValue(backendId, out var backend)) throw new InvalidDataException($"Profile {ReadString(profile, "id")} references an unknown backend.");
            var model = models.OfType<JsonObject>().Single(item => ReadString(item, "id") == ReadString(profile, "model_id"));
            var required = model["required_capabilities"] as JsonArray ?? [];
            var provided = backend["capabilities"] as JsonArray ?? [];
            if (required.Any(capability => !provided.Any(item => string.Equals(item?.GetValue<string>(), capability?.GetValue<string>(), StringComparison.Ordinal))))
                throw new InvalidDataException($"Backend {backendId} does not provide all model capabilities for profile {ReadString(profile, "id")}.");
            foreach (var adapter in profile["adapters"] as JsonArray ?? [])
            {
                var assetId = adapter?["asset_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(assetId) || !assets.Contains(assetId)) throw new InvalidDataException($"Profile {ReadString(profile, "id")} has an adapter without a valid asset id.");
            }
        }

        foreach (var hardwareProfile in HardwareProfiles.Profiles)
        {
            if (!modelIds.Contains(HardwareProfiles.ModelId)) throw new InvalidDataException("hardware_profiles.model_id is unknown.");
            if (!profileById.ContainsKey(hardwareProfile.RuntimeProfileId)) throw new InvalidDataException($"Hardware profile {hardwareProfile.Id} selects an unknown runtime profile.");
            if (hardwareProfile.FallbackProfileIds.Any(id => !profileById.ContainsKey(id))) throw new InvalidDataException($"Hardware profile {hardwareProfile.Id} references an unknown fallback profile.");
            if (hardwareProfile.SelectableProfileIds.Any(id => !profileById.ContainsKey(id))) throw new InvalidDataException($"Hardware profile {hardwareProfile.Id} exposes an unknown runtime profile.");
        }
    }

    private static string ReadString(JsonObject value, string property) => value[property]?.GetValue<string>()
        ?? throw new InvalidDataException($"GitHub manifest reference missing string property {property}.");

    public DownloadAsset GetAsset(string id) => Assets.SingleOrDefault(asset => asset.Id == id)
                                                     ?? throw new InvalidDataException($"GitHub manifest 缺少 asset: {id}");
}

internal sealed class DownloadAsset
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("target_relative_path")] public string TargetRelativePath { get; init; } = "";
    [JsonPropertyName("archive_format")] public string ArchiveFormat { get; init; } = "none";
    [JsonPropertyName("unpacked_bytes_estimate")] public long UnpackedBytes { get; init; }
    [JsonPropertyName("revision")] public string Revision { get; init; } = "";
    [JsonPropertyName("license")] public string License { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Kind)) throw new InvalidDataException("Asset id/kind 不可為空。");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException($"Asset {Id} 必須使用 HTTPS URL。");
        if (SizeBytes <= 0) throw new InvalidDataException($"Asset {Id} size_bytes 必須大於零。");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"Asset {Id} 缺少有效 SHA-256。");
        if (string.IsNullOrWhiteSpace(TargetRelativePath)) throw new InvalidDataException($"Asset {Id} 缺少 target_relative_path。");
        if (ArchiveFormat is not ("none" or "zip")) throw new InvalidDataException($"Asset {Id} 的 archive_format 目前只支援 none/zip。");
        if (ArchiveFormat == "zip" && UnpackedBytes <= 0) throw new InvalidDataException($"ZIP asset {Id} 需要 unpacked_bytes。");
    }
}

internal sealed class BootstrapSettings
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("manifest_url")] public string ManifestUrl { get; init; } = "";
    [JsonPropertyName("manifest_channel")] public string ManifestChannel { get; init; } = "stable";
    [JsonPropertyName("default_install_dir")] public string DefaultInstallDirectory { get; init; } = "%LOCALAPPDATA%\\BonsaiLocal";

    public static BootstrapSettings Load()
    {
        var sidecar = Path.Combine(AppContext.BaseDirectory, "bootstrap.json");
        string json;
        if (File.Exists(sidecar))
        {
            json = File.ReadAllText(sidecar);
        }
        else
        {
            using var stream = typeof(BootstrapSettings).Assembly.GetManifestResourceStream("BonsaiSetup.bootstrap.json")
                               ?? throw new InvalidDataException("找不到 bootstrap.json。");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        var settings = JsonSerializer.Deserialize<BootstrapSettings>(json, JsonOptions)
                       ?? throw new InvalidDataException("bootstrap.json 無法解析。");
        if (settings.SchemaVersion != 1) throw new InvalidDataException($"不支援的 bootstrap schema: {settings.SchemaVersion}");
        if (!string.IsNullOrWhiteSpace(settings.ManifestUrl)
            && (!Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("bootstrap.json manifest_url 必須是 HTTPS URL。");
        return settings;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
