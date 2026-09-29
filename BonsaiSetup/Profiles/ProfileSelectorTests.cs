using BonsaiSetup.Hardware;

namespace BonsaiSetup.Profiles;

internal static class ProfileSelectorTests
{
    public static int Run()
    {
        var catalog = ProfileSelector.LoadEmbedded();
        var cases = new (string Name, HardwareSnapshot Hardware, string Expected)[]
        {
            ("RTX 5060 8 GB + 32 GB RAM selects hybrid", Make("NVIDIA", 7960, 32768), "nvidia_8gb"),
            ("RTX 5070 12 GB selects 12 GB class", Make("NVIDIA", 12227, 32768), "nvidia_12gb"),
            ("16 GB NVIDIA selects 16 GB+ class", Make("NVIDIA", 16280, 65536), "nvidia_16gb_plus"),
            ("sub-8 GB NVIDIA is retained as low VRAM", Make("NVIDIA", 6144, 32768), "low_vram"),
            ("no discrete GPU uses CPU profile", Make(null, 0, 32768), "cpu_only"),
            ("non-NVIDIA GPU stays installable on CPU path", Make("AMD", 12288, 32768), "cpu_only")
        };

        var failed = 0;
        foreach (var test in cases)
        {
            var actual = ProfileSelector.Select(test.Hardware, catalog);
            var passed = actual.ProfileId == test.Expected;
            if (test.Name.StartsWith("RTX 5060", StringComparison.Ordinal) && actual.MemoryStrategy != "hybrid_gpu_system_ram") passed = false;
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} · {test.Name} · {actual.ProfileId} · {actual.MemoryStrategy}");
            if (!passed) failed++;
        }

        var manual = ProfileSelector.Select(Make("NVIDIA", 12227, 32768), catalog, "nvidia_8gb");
        var manualPassed = manual.ProfileId == "nvidia_8gb" && manual.IsManualOverride;
        Console.WriteLine($"{(manualPassed ? "PASS" : "FAIL")} · --profile override remains available · {manual.ProfileId}");
        if (!manualPassed) failed++;

        Console.WriteLine($"Selector checks: {cases.Length + 1 - failed}/{cases.Length + 1} passed.");
        return failed == 0 ? 0 : 3;
    }

    private static HardwareSnapshot Make(string? vendor, long vramMiB, long ramMiB)
    {
        var gpus = vendor is null ? [] : new List<GpuInformation>
        {
            new() { Name = $"Test {vendor} GPU", Vendor = vendor, VendorId = vendor == "NVIDIA" ? "10DE" : "1002", DedicatedMemoryMiB = vramMiB, Source = "fixture" }
        };
        return new HardwareSnapshot
        {
            Gpus = gpus,
            Memory = new MemoryInformation { TotalBytes = (ulong)ramMiB * 1024UL * 1024UL, AvailableBytes = (ulong)ramMiB * 1024UL * 1024UL },
            InstallDisk = new DiskInformation { AvailableBytes = 50_000_000_000 }
        };
    }
}
