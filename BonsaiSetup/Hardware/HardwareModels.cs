using System.Text.Json.Serialization;

namespace BonsaiSetup.Hardware;

internal sealed class HardwareSnapshot
{
    [JsonPropertyName("os")] public OsInformation Os { get; init; } = new();
    [JsonPropertyName("cpu")] public CpuInformation Cpu { get; init; } = new();
    [JsonPropertyName("memory")] public MemoryInformation Memory { get; init; } = new();
    [JsonPropertyName("gpus")] public List<GpuInformation> Gpus { get; init; } = [];
    [JsonPropertyName("gpu_detection_source")] public string GpuDetectionSource { get; init; } = "";
    [JsonPropertyName("install_disk")] public DiskInformation InstallDisk { get; init; } = new();
}

internal sealed class OsInformation
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("build")] public int Build { get; init; }
}

internal sealed class CpuInformation
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("physical_cores")] public int PhysicalCores { get; init; }
    [JsonPropertyName("logical_threads")] public int LogicalThreads { get; init; }
    [JsonPropertyName("avx2")] public bool Avx2 { get; init; }
    [JsonPropertyName("avx512")] public bool Avx512 { get; init; }
}

internal sealed class MemoryInformation
{
    [JsonPropertyName("total_bytes")] public ulong TotalBytes { get; init; }
    [JsonPropertyName("available_bytes")] public ulong AvailableBytes { get; init; }
    [JsonIgnore] public long TotalMiB => checked((long)(TotalBytes / (1024UL * 1024UL)));
}

internal sealed class GpuInformation
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("vendor")] public string Vendor { get; init; } = "Unknown";
    [JsonPropertyName("vendor_id")] public string VendorId { get; init; } = "";
    [JsonPropertyName("dedicated_memory_mib")] public long DedicatedMemoryMiB { get; init; }
    [JsonPropertyName("driver_version")] public string DriverVersion { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
}

internal sealed class DiskInformation
{
    [JsonPropertyName("root")] public string Root { get; init; } = "";
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("available_bytes")] public long AvailableBytes { get; init; }
    [JsonPropertyName("install_directory")] public string InstallDirectory { get; init; } = "";
}
