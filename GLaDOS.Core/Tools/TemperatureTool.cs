using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GLaDOS.Core.Tools;

public class TemperatureTool : IAgentTool
{
    public string Name => "get_temperature";

    public string Description =>
        "Retrieves the current CPU and GPU temperatures. Use this whenever the user asks for the current CPU or GPU temperatures";

    public ToolPermission Permitted => ToolPermission.User;

    public JsonObject Parameters => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject() // No arguments needed for temperature retrieval
    };

public Task<string> ExecuteAsync(JsonObject arguments)
    {
        string cpuTemp = "Unknown";
        string gpuTemp = "Unknown";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            cpuTemp = GetLinuxCpuTemperature();
            gpuTemp = GetLinuxGpuTemperature();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            cpuTemp = GetWindowsCpuTemperature();
            gpuTemp = GetWindowsGpuTemperature();
        }

        var resultObj = new
        {
            cpu_temperature = cpuTemp,
            gpu_temperature = gpuTemp
        };

        return Task.FromResult(JsonSerializer.Serialize(resultObj));
    }

    #region Linux Implementations
    private string GetLinuxCpuTemperature()
    {
        // Reading directly from sysfs is faster and more reliable than parsing 'sensors'
        if (File.Exists("/sys/class/thermal/thermal_zone0/temp"))
        {
            try
            {
                string raw = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp").Trim();
                if (double.TryParse(raw, out double millidegrees))
                {
                    return $"{millidegrees / 1000:F1}°C";
                }
            }
            catch { }
        }
        return "Unknown";
    }

    private string GetLinuxGpuTemperature()
    {
        // Fallback to nvidia-smi if Nvidia, otherwise your existing sensors logic for AMD
        string nvidiaTemp = RunCommand("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(nvidiaTemp) && !nvidiaTemp.Contains("not found"))
        {
            return $"{nvidiaTemp.Trim()}°C";
        }

        return "Unknown"; // Add your AMD/sensors parsing here if needed
    }
    #endregion

    #region Windows Implementations
    private string GetWindowsCpuTemperature()
    {
        // WARNING: Requires your application to run as Administrator
        // Queries WMI for the MSAcpi_ThermalZoneTemperature
        string lmInfo = RunCommand("powershell", "-Command \"Get-CimInstance -Namespace root/wmi -ClassName MsAcpi_ThermalZoneTemperature | Select-Object -ExpandProperty CurrentTemperature\"");
        
        if (double.TryParse(lmInfo.Trim(), out double kelvinTenths))
        {
            // Convert tenths of Kelvin to Celsius
            double celsius = (kelvinTenths / 10.0) - 273.15;
            return $"{celsius:F1}°C";
        }
        return "Unknown (Requires Admin)";
    }

    private string GetWindowsGpuTemperature()
    {
        // Checks Nvidia GPUs via standard nvidia-smi path on Windows
        string nvSmiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        
        if (File.Exists(nvSmiPath))
        {
            string output = RunCommand(nvSmiPath, "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
            if (int.TryParse(output.Trim(), out int temp))
            {
                return $"{temp}°C";
            }
        }
        return "Unknown";
    }
    #endregion

    private string RunCommand(string filename, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
