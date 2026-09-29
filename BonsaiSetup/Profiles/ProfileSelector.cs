using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BonsaiSetup.Hardware;

namespace BonsaiSetup.Profiles;

internal static class ProfileSelector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ProfileCatalog LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("BonsaiSetup.hardware-profiles.json")
                           ?? throw new InvalidDataException("找不到內嵌 hardware-profiles.json。");
        return JsonSerializer.Deserialize<ProfileCatalog>(stream, JsonOptions)
               ?? throw new InvalidDataException("hardware-profiles.json 無法解析。");
    }

    public static ProfileSelection Select(HardwareSnapshot hardware, ProfileCatalog catalog, string? forcedProfileId = null)
    {
        if (catalog.SchemaVersion != 1 || catalog.Profiles.Count == 0) throw new InvalidDataException("不支援或空白的 hardware profile catalog。");

        ProfileRule selected;
        var forced = !string.IsNullOrWhiteSpace(forcedProfileId);
        if (forced)
        {
            selected = catalog.Profiles.SingleOrDefault(profile => string.Equals(profile.Id, forcedProfileId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"Profile 不存在：{forcedProfileId}");
        }
        else
        {
            var nvidia = hardware.Gpus
                .Where(gpu => string.Equals(gpu.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(gpu => gpu.DedicatedMemoryMiB)
                .FirstOrDefault();

            selected = nvidia is null
                ? catalog.Profiles.Single(profile => profile.Id == "cpu_only")
                : catalog.Profiles
                    .Where(profile => string.Equals(profile.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase)
                                      && nvidia.DedicatedMemoryMiB >= profile.MinVramMiB
                                      && (profile.MaxVramMiB is null || nvidia.DedicatedMemoryMiB <= profile.MaxVramMiB))
                    .OrderByDescending(profile => profile.MinVramMiB)
                    .FirstOrDefault()
                  ?? catalog.Profiles.Single(profile => profile.Id == "low_vram");
        }

        var selectedGpu = hardware.Gpus
            .Where(gpu => string.Equals(gpu.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(gpu => gpu.DedicatedMemoryMiB)
            .FirstOrDefault();
        var warnings = new List<string>();
        if (selectedGpu is null && hardware.Gpus.Count > 0)
            warnings.Add("偵測到非 NVIDIA GPU；目前 Bonsai runtime profile 以 CPU 路徑作為相容 fallback，安裝不會因 GPU 廠牌而拒絕。");
        if (selectedGpu is not null && selectedGpu.DedicatedMemoryMiB < 7500)
            warnings.Add("VRAM 位於低容量區間，需由 profile 使用較多系統 RAM/CPU；VRAM 不足不會阻止安裝。");
        if (hardware.Memory.TotalBytes < selected.RecommendedRamBytes)
            warnings.Add($"系統 RAM 低於此 profile 的建議值 {selected.RecommendedRamBytes / 1_000_000_000d:0.#} GB；安裝仍可繼續，第一次啟動可能需更低 context 或 CPU offload。");

        return new ProfileSelection
        {
            ProfileId = selected.Id,
            DisplayName = selected.DisplayName,
            ModelId = catalog.ModelId,
            RuntimeProfileId = selected.RuntimeProfileId,
            MemoryStrategy = selected.MemoryStrategy,
            ContextPreference = selected.ContextPreference,
            SelectedGpu = selectedGpu,
            IsManualOverride = forced,
            FallbackProfileIds = [.. selected.FallbackProfileIds],
            AvailableProfileIds = [.. selected.SelectableProfileIds],
            Warnings = warnings
        };
    }
}

internal sealed class ProfileCatalog
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("model_id")] public string ModelId { get; init; } = "";
    [JsonPropertyName("profiles")] public List<ProfileRule> Profiles { get; init; } = [];
}

internal sealed class ProfileRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("vendor")] public string Vendor { get; init; } = "";
    [JsonPropertyName("min_vram_mib")] public long MinVramMiB { get; init; }
    [JsonPropertyName("max_vram_mib")] public long? MaxVramMiB { get; init; }
    [JsonPropertyName("memory_strategy")] public string MemoryStrategy { get; init; } = "";
    [JsonPropertyName("recommended_ram_bytes")] public ulong RecommendedRamBytes { get; init; }
    [JsonPropertyName("context_preference")] public string ContextPreference { get; init; } = "stable";
    [JsonPropertyName("runtime_profile_id")] public string RuntimeProfileId { get; init; } = "";
    [JsonPropertyName("fallback_profile_ids")] public List<string> FallbackProfileIds { get; init; } = [];
    [JsonPropertyName("selectable_profile_ids")] public List<string> SelectableProfileIds { get; init; } = [];
}

internal sealed class ProfileSelection
{
    [JsonPropertyName("profile_id")] public string ProfileId { get; init; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; init; } = "";
    [JsonPropertyName("runtime_profile_id")] public string RuntimeProfileId { get; init; } = "";
    [JsonPropertyName("memory_strategy")] public string MemoryStrategy { get; init; } = "";
    [JsonPropertyName("context_preference")] public string ContextPreference { get; init; } = "";
    [JsonPropertyName("selected_gpu")] public GpuInformation? SelectedGpu { get; init; }
    [JsonPropertyName("manual_override")] public bool IsManualOverride { get; init; }
    [JsonPropertyName("fallback_profile_ids")] public List<string> FallbackProfileIds { get; init; } = [];
    [JsonPropertyName("available_profile_ids")] public List<string> AvailableProfileIds { get; init; } = [];
    [JsonPropertyName("warnings")] public List<string> Warnings { get; init; } = [];
}
