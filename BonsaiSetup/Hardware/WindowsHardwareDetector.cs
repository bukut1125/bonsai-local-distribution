using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace BonsaiSetup.Hardware;

internal static class WindowsHardwareDetector
{
    public static HardwareSnapshot Detect(string installDirectory, bool forceDxgi = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("BonsaiSetup 目前只支援 Windows。");

        var os = NativeWindows.GetVersion();
        var memory = NativeWindows.GetMemoryStatus();
        var cpu = DetectCpu();
        var (gpus, gpuSource) = DetectGpus(forceDxgi);
        var disk = DetectInstallDisk(installDirectory);

        return new HardwareSnapshot
        {
            Os = new OsInformation { Name = "Windows", Version = $"{os.Major}.{os.Minor}.{os.Build}", Build = os.Build },
            Cpu = cpu,
            Memory = new MemoryInformation { TotalBytes = memory.TotalPhysical, AvailableBytes = memory.AvailablePhysical },
            Gpus = gpus,
            GpuDetectionSource = gpuSource,
            InstallDisk = disk
        };
    }

    private static CpuInformation DetectCpu()
    {
        var name = "Unknown CPU";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? name;
        }
        catch (UnauthorizedAccessException) { }

        return new CpuInformation
        {
            Name = name,
            PhysicalCores = NativeWindows.GetPhysicalCoreCount(),
            LogicalThreads = NativeWindows.GetLogicalProcessorCount(),
            Avx2 = Avx2.IsSupported,
            Avx512 = Avx512F.IsSupported
        };
    }

    private static (List<GpuInformation> Gpus, string Source) DetectGpus(bool forceDxgi)
    {
        if (!forceDxgi)
        {
            var nvidia = TryNvidiaSmi();
            if (nvidia.Count > 0) return (nvidia, "nvidia-smi");
        }
        return (DxgiAdapterEnumerator.Enumerate(), "DXGI");
    }

    private static List<GpuInformation> TryNvidiaSmi()
    {
        var executable = FindNvidiaSmi();
        if (executable is null) return [];

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            if (!process.Start()) return [];
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            if (process.ExitCode != 0) return [];
            var devices = new List<GpuInformation>();
            foreach (var line in stdoutTask.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var columns = line.Split(',').Select(item => item.Trim()).ToArray();
                if (columns.Length < 3) continue;
                var digits = new string(columns[1].Where(char.IsDigit).ToArray());
                if (!long.TryParse(digits, out var mib)) continue;
                devices.Add(new GpuInformation
                {
                    Name = columns[0],
                    Vendor = "NVIDIA",
                    VendorId = "10DE",
                    DedicatedMemoryMiB = mib,
                    DriverVersion = columns[2],
                    Source = "nvidia-smi"
                });
            }
            return devices;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or TaskCanceledException)
        {
            return [];
        }
    }

    private static string? FindNvidiaSmi()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "nvidia-smi.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var common = Path.Combine(system32, "nvidia-smi.exe");
        if (File.Exists(common)) return common;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var nvidia = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(nvidia) ? nvidia : null;
    }

    private static DiskInformation DetectInstallDisk(string installDirectory)
    {
        var fullPath = Path.GetFullPath(installDirectory);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException($"無法解析安裝磁碟：{fullPath}");
        var drive = DriveInfo.GetDrives().FirstOrDefault(item => string.Equals(item.Name, root, StringComparison.OrdinalIgnoreCase));
        if (drive is null || !drive.IsReady) throw new IOException($"安裝磁碟不可用：{root}");
        return new DiskInformation
        {
            Root = root,
            TotalBytes = drive.TotalSize,
            AvailableBytes = drive.AvailableFreeSpace,
            InstallDirectory = fullPath
        };
    }
}

internal static class NativeWindows
{
    private const ushort AllProcessorGroups = 0xFFFF;
    private const uint RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    public static Version GetVersion()
    {
        var info = new RtlOsVersionInfo { Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>() };
        var status = RtlGetVersion(ref info);
        if (status != 0) Marshal.ThrowExceptionForHR(status);
        return new Version((int)info.Major, (int)info.Minor, (int)info.Build);
    }

    public static (ulong TotalPhysical, ulong AvailablePhysical) GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx 無法讀取實體記憶體。");
        return (status.TotalPhysical, status.AvailablePhysical);
    }

    public static int GetLogicalProcessorCount()
    {
        var count = GetActiveProcessorCount(AllProcessorGroups);
        return count == 0 ? Environment.ProcessorCount : checked((int)count);
    }

    public static int GetPhysicalCoreCount()
    {
        uint bytes = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref bytes);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytes == 0) return Environment.ProcessorCount;

        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref bytes)) return Environment.ProcessorCount;
            var end = buffer + checked((int)bytes);
            var current = buffer;
            var cores = 0;
            while (current + 8 <= end)
            {
                var relation = unchecked((uint)Marshal.ReadInt32(current));
                var recordSize = unchecked((uint)Marshal.ReadInt32(current, 4));
                if (recordSize < 8 || current + checked((int)recordSize) > end) break;
                if (relation == RelationProcessorCore) cores++;
                current += checked((int)recordSize);
            }
            return cores > 0 ? cores : Environment.ProcessorCount;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

internal static class DxgiAdapterEnumerator
{
    private static readonly Guid Factory1Id = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapterFlag = 2;

    public static List<GpuInformation> Enumerate()
    {
        var factoryId = Factory1Id;
        var hr = CreateDXGIFactory1(ref factoryId, out var factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            var result = new List<GpuInformation>();
            var enumAdapters = GetDelegate<EnumAdapters1>(factory, 12);
            for (uint index = 0; ; index++)
            {
                var status = enumAdapters(factory, index, out var adapter);
                if (status == DxgiErrorNotFound) break;
                if (status < 0) Marshal.ThrowExceptionForHR(status);
                try
                {
                    var getDescription = GetDelegate<GetDescription1>(adapter, 10);
                    if (getDescription(adapter, out var description) < 0) continue;
                    if ((description.Flags & SoftwareAdapterFlag) != 0) continue;
                    var vendor = description.VendorId switch
                    {
                        0x10DE => "NVIDIA",
                        0x1002 => "AMD",
                        0x8086 => "Intel",
                        _ => "Unknown"
                    };
                    result.Add(new GpuInformation
                    {
                        Name = description.Description.TrimEnd('\0'),
                        Vendor = vendor,
                        VendorId = description.VendorId.ToString("X4"),
                        DedicatedMemoryMiB = checked((long)(description.DedicatedVideoMemory.ToUInt64() / (1024UL * 1024UL))),
                        DriverVersion = "",
                        Source = "DXGI"
                    });
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            return result;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static T GetDelegate<T>(IntPtr instance, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription1(IntPtr self, out DxgiAdapterDescription1 description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubsystemId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
