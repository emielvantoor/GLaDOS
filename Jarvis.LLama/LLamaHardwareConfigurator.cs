using System.Runtime.InteropServices;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Configuration;

namespace Jarvis.LLama;

public static class LLamaHardwareConfigurator
{
    private static bool _isConfigured = false;
    private static bool _useGpu = false;

    /// <summary>
    /// Configures the hardware settings based on the provided configuration.
    /// </summary>
    /// <param name="configuration">The configuration object containing hardware settings.</param>
    public static void Configure(IConfiguration configuration)
    {
        if (_isConfigured) return;

        var hardwareMode = configuration["Jarvis:HardwareMode"] ?? "CPU";
        _useGpu = hardwareMode.Equals("GPU", StringComparison.OrdinalIgnoreCase);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string osFolder;
        string libExtension;
        string prefix = "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            osFolder = "win-x64";
            libExtension = ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            osFolder = "linux-x64";
            libExtension = ".so";
            prefix = "lib"; // Belangrijk voor Linux (.so bestanden)
        }
        else
        {
            osFolder = "osx-x64";
            libExtension = ".dylib";
            prefix = "lib";
        }

        var nativeRootDir = Path.Combine(baseDir, "runtimes", osFolder, "native");

        if (_useGpu)
        {
            var cudaDir = Path.Combine(nativeRootDir, "cuda12");
            var vulkanDir = Path.Combine(nativeRootDir, "vulkan");
            var cpuDir = Path.Combine(nativeRootDir, "avx2");

            string chosenGpuDir = "";
            string gpuTechnology = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (Directory.Exists(cudaDir) && File.Exists(Path.Combine(cudaDir, "libllama.so")))
                {
                    if (File.Exists("/usr/bin/nvidia-smi"))
                    {
                        chosenGpuDir = cudaDir;
                        gpuTechnology = "CUDA 12 (NVIDIA)";
                    }
                    else
                    {
                        chosenGpuDir = vulkanDir;
                        gpuTechnology = "Vulkan (AMD/Universal)";
                    }
                }
                else
                {
                    chosenGpuDir = vulkanDir;
                    gpuTechnology = "Vulkan (AMD/Universal)";
                }

                // --- DE DEFINITIEVE LINUX FIX ---
                // Kopieer libggml-cpu.so naar de actieve GPU map als hij daar nog niet staat.
                // Dit voorkomt de 'libggml-cpu.so => not found' fout van de Linux linker.
                var targetCpuLib = Path.Combine(chosenGpuDir, "libggml-cpu.so");
                var sourceCpuLib = Path.Combine(cpuDir, "libggml-cpu.so");

                if (!File.Exists(targetCpuLib) && File.Exists(sourceCpuLib))
                {
                    try
                    {
                        File.Copy(sourceCpuLib, targetCpuLib, overwrite: true);
                        Console.WriteLine(
                            $"[Jarvis] Linker-fix: libggml-cpu.so succesvol gekopieerd naar {gpuTechnology} map.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Jarvis] Waarschuwing: Kon libggml-cpu.so niet kopiëren: {ex.Message}");
                    }
                }
                // ---------------------------------
            }
            else
            {
                chosenGpuDir = vulkanDir;
                gpuTechnology = "Vulkan";
            }

            var llamaPath = Path.Combine(chosenGpuDir, $"{prefix}llama{libExtension}");
            if (!File.Exists(llamaPath))
            {
                llamaPath = Path.Combine(chosenGpuDir, $"llama{libExtension}");
            }

            if (File.Exists(llamaPath))
            {
                var ggmlPath = Path.Combine(chosenGpuDir, $"{prefix}ggml{libExtension}");
                if (!File.Exists(ggmlPath))
                {
                    ggmlPath = Path.Combine(chosenGpuDir, $"ggml{libExtension}");
                }

                if (!File.Exists(ggmlPath) && gpuTechnology.Contains("CUDA") &&
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    ggmlPath = Path.Combine(chosenGpuDir, "libggml-cuda.so");
                }

                var finalBackend = File.Exists(ggmlPath) ? ggmlPath : null;

                NativeLibraryConfig.Instance.WithLibrary(llamaPath, finalBackend);
                Console.WriteLine($"[Jarvis] GPU hardware gedetecteerd! Succesvol {gpuTechnology} geselecteerd.");
            }
            else
            {
                Console.WriteLine($"[Jarvis] GPU binaries niet gevonden in {chosenGpuDir}. Fallback naar CPU.");
                ConfigureCpuFallback(nativeRootDir, libExtension, prefix);
            }
        }
        else
        {
            ConfigureCpuFallback(nativeRootDir, libExtension, prefix);
        }

        _isConfigured = true;
    }

    private static void ConfigureCpuFallback(string nativeRootDir, string libExtension, string prefix)
    {
        var cpuDir = Path.Combine(nativeRootDir, "avx2");
        var llamaPath = Path.Combine(cpuDir, $"{prefix}llama{libExtension}");
        var ggmlPath = Path.Combine(cpuDir, $"{prefix}ggml{libExtension}");

        if (File.Exists(llamaPath))
        {
            var finalBackend = File.Exists(ggmlPath) ? ggmlPath : null;
            NativeLibraryConfig.Instance.WithLibrary(llamaPath, finalBackend);
            Console.WriteLine($"[Jarvis] CPU AVX2 ({prefix}llama{libExtension}) geselecteerd.");
        }
        else
        {
            NativeLibraryConfig.Instance.WithLibrary(prefix == "lib" ? "libllama.so" : "llama.dll", null);
            Console.WriteLine($"[Jarvis] Lokale CPU binaries niet gevonden, gebruik gemaakt van systeem-fallback.");
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static ModelParams CreateOptimizedParameters(IConfiguration configuration)
    {
        // 1. Dwing de configurator EERST de juiste .so/.dll paden te registreren!
        Configure(configuration);

        // 2. Haal pas DAARNA de waarden op
        var modelPath = configuration["Jarvis:ModelPath"] ??
                        throw new ArgumentNullException("ModelPath is niet ingesteld in appsettings.json");
        var contextSize = uint.TryParse(configuration["Jarvis:ContextSize"], out var size) ? size : 3072;

        var cpuCores = Environment.ProcessorCount / 2;
        if (cpuCores <= 0) cpuCores = 4;

        // 3. Nu zal de constructor niet meer crashen omdat de native paden bekend zijn
        return new ModelParams(modelPath)
        {
            ContextSize = contextSize,
            Threads = cpuCores,
            GpuLayerCount = _useGpu ? 99 : 0,
            TypeK = GGMLType.GGML_TYPE_Q8_0,
            TypeV = GGMLType.GGML_TYPE_Q8_0,
        };
    }
}