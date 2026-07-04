using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class RuntimeMemoryUsageResponse
{
    [JsonPropertyName("process_ram_mb")]
    public double ProcessRamMb { get; set; }

    [JsonPropertyName("managed_heap_mb")]
    public double ManagedHeapMb { get; set; }

    [JsonPropertyName("system_ram_used_mb")]
    public double? SystemRamUsedMb { get; set; }

    [JsonPropertyName("system_ram_total_mb")]
    public double? SystemRamTotalMb { get; set; }

    [JsonPropertyName("gpu_vram_used_mb")]
    public double? GpuVramUsedMb { get; set; }

    [JsonPropertyName("gpu_vram_total_mb")]
    public double? GpuVramTotalMb { get; set; }

    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("gpu_source")]
    public string? GpuSource { get; set; }
}
