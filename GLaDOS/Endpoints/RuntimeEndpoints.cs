using System.Diagnostics;
using System.Globalization;
using GLaDOS.Models;

namespace GLaDOS.Endpoints;

public static class RuntimeEndpoints
{
    public static void MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/v1").MapGet("/runtime/memory", GetRuntimeMemoryUsage);
    }

    private static IResult GetRuntimeMemoryUsage()
    {
        var process = Process.GetCurrentProcess();
        var response = new RuntimeMemoryUsageResponse
        {
            ProcessRamMb = BytesToMegabytes(process.WorkingSet64),
            ManagedHeapMb = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: false))
        };

        if (TryGetSystemMemory(out var systemUsedMb, out var systemTotalMb))
        {
            response.SystemRamUsedMb = systemUsedMb;
            response.SystemRamTotalMb = systemTotalMb;
        }

        if (TryGetNvidiaMemory(out var nvidiaUsedMb, out var nvidiaTotalMb, out var nvidiaName))
        {
            response.GpuVramUsedMb = nvidiaUsedMb;
            response.GpuVramTotalMb = nvidiaTotalMb;
            response.GpuName = nvidiaName;
            response.GpuSource = "nvidia-smi";
        }
        else if (TryGetRocmMemory(out var rocmUsedMb, out var rocmTotalMb, out var rocmName))
        {
            response.GpuVramUsedMb = rocmUsedMb;
            response.GpuVramTotalMb = rocmTotalMb;
            response.GpuName = rocmName;
            response.GpuSource = "rocm-smi";
        }

        return Results.Ok(response);
    }

    private static bool TryGetSystemMemory(out double usedMb, out double totalMb)
    {
        usedMb = 0;
        totalMb = 0;
        if (!File.Exists("/proc/meminfo")) return false;

        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => ParseFirstNumber(parts[1]), StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("MemTotal", out var totalKb)) return false;

        values.TryGetValue("MemAvailable", out var availableKb);
        totalMb = totalKb / 1024;
        usedMb = (totalKb - availableKb) / 1024;
        return true;
    }

    private static bool TryGetNvidiaMemory(out double usedMb, out double totalMb, out string? gpuName)
    {
        usedMb = 0;
        totalMb = 0;
        gpuName = null;
        var output = RunCommand("nvidia-smi", "--query-gpu=name,memory.used,memory.total --format=csv,noheader,nounits");
        var firstLine = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return false;

        var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out usedMb) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out totalMb))
        {
            return false;
        }

        gpuName = parts[0];
        return true;
    }

    private static bool TryGetRocmMemory(out double usedMb, out double totalMb, out string? gpuName)
    {
        usedMb = 0;
        totalMb = 0;
        gpuName = null;
        var totalOutput = RunCommand("rocm-smi", "--showmeminfo vram --csv");
        if (!TryParseRocmVramInfo(totalOutput, out var device, out usedMb, out totalMb)) return false;

        gpuName = ParseRocmGpuName(RunCommand("rocm-smi", "--showproductname --csv"), device);
        return true;
    }

    private static bool TryParseRocmVramInfo(string? output, out string? device, out double usedMb, out double totalMb)
    {
        device = null;
        usedMb = 0;
        totalMb = 0;
        if (string.IsNullOrWhiteSpace(output)) return false;

        var bestTotalBytes = 0d;
        var bestUsedBytes = 0d;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("card", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalBytes) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedBytes) ||
                totalBytes <= bestTotalBytes)
            {
                continue;
            }

            device = parts[0];
            bestTotalBytes = totalBytes;
            bestUsedBytes = usedBytes;
        }

        if (bestTotalBytes <= 0) return false;
        usedMb = BytesToMegabytes((long)bestUsedBytes);
        totalMb = BytesToMegabytes((long)bestTotalBytes);
        return true;
    }

    private static string? ParseRocmGpuName(string? output, string? device)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(device) || !line.StartsWith(device, StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;

            if (!string.IsNullOrWhiteSpace(parts[1]) && !parts[1].Equals("N/A", StringComparison.OrdinalIgnoreCase)) return parts[1];
            if (!string.IsNullOrWhiteSpace(parts[2]) && !parts[2].Equals("N/A", StringComparison.OrdinalIgnoreCase)) return parts[2];
        }

        return null;
    }

    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return process != null && process.WaitForExit(1500) && process.ExitCode == 0
                ? process.StandardOutput.ReadToEnd()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static double ParseFirstNumber(string text)
    {
        var number = new string(text.SkipWhile(c => !char.IsDigit(c) && c != '.')
            .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double BytesToMegabytes(long bytes) => bytes / 1024d / 1024d;
}
