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
            var rocmDir = Path.Combine(nativeRootDir, "rocm"); // <-- NIEUWE ROCM MAP
            var vulkanDir = Path.Combine(nativeRootDir, "vulkan");
            var cpuDir = Path.Combine(nativeRootDir, "avx2");

            string chosenGpuDir = "";
            string gpuTechnology = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // 1. Check eerst op NVIDIA CUDA
                if (Directory.Exists(cudaDir) && File.Exists(Path.Combine(cudaDir, "libllama.so")) && File.Exists("/usr/bin/nvidia-smi"))
                {
                    chosenGpuDir = cudaDir;
                    gpuTechnology = "CUDA 12 (NVIDIA)";
                }
                // 2. Check daarna op AMD ROCm (Native Arch Linux setup of via lokale map)
                else if (File.Exists("/dev/kfd") || Directory.Exists("/opt/rocm") || File.Exists("/usr/bin/rocminfo"))
                {
                    // Als de 'rocm' map lokaal in je runtimes staat gebruiken we die, 
                    // anders vallen we terug op het systeem (/usr/lib) waar yay/pacman hem heeft neergezet.
                    if (Directory.Exists(rocmDir) && File.Exists(Path.Combine(rocmDir, "libllama.so")))
                    {
                        chosenGpuDir = rocmDir;
                        gpuTechnology = "ROCm/HIP (AMD lokaal)";
                    }
                    else if (File.Exists("/usr/lib/libllama.so"))
                    {
                        // Systeem-brede installatie via Arch `llama.cpp-rocm`
                        chosenGpuDir = "/usr/lib";
                        gpuTechnology = "ROCm/HIP (AMD Systeem)";
                    }
                    else
                    {
                        // Geen ROCm binaries gevonden? Fallback naar Vulkan
                        chosenGpuDir = vulkanDir;
                        gpuTechnology = "Vulkan (AMD Fallback)";
                    }
                }
                // 3. Geen Nvidia en geen AMD ROCm? Gebruik universele Vulkan
                else
                {
                    chosenGpuDir = vulkanDir;
                    gpuTechnology = "Vulkan (Universal)";
                }

                // --- DE DEFINITIEVE LINUX FIX VOOR COPIËREN ---
                // Alleen uitvoeren als we een lokale map gebruiken (niet bij de gedeelde /usr/lib)
                if (chosenGpuDir != "/usr/lib")
                {
                    var targetCpuLib = Path.Combine(chosenGpuDir, "libggml-cpu.so");
                    var sourceCpuLib = Path.Combine(cpuDir, "libggml-cpu.so");

                    if (!File.Exists(targetCpuLib) && File.Exists(sourceCpuLib))
                    {
                        try
                        {
                            File.Copy(sourceCpuLib, targetCpuLib, overwrite: true);
                            Console.WriteLine($"[Jarvis] Linker-fix: libggml-cpu.so succesvol gekopieerd naar {gpuTechnology} map.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Jarvis] Waarschuwing: Kon libggml-cpu.so niet kopiëren: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                // Windows / OSX logiek (Vulkan fallback)
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

                // Specifieke afhandeling voor CUDA en ROCm backends onder Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (gpuTechnology.Contains("CUDA"))
                    {
                        ggmlPath = Path.Combine(chosenGpuDir, "libggml-cuda.so");
                    }
                    else if (gpuTechnology.Contains("ROCm"))
                    {
                        ggmlPath = Path.Combine(chosenGpuDir, "libggml-hip.so"); // of libggml-hip.so afhankelijk van je build
                    }
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
            // Check of er een systeem-brede library staat (handig voor Arch)
            if (File.Exists("/usr/lib/libllama.so") && prefix == "lib")
            {
                NativeLibraryConfig.Instance.WithLibrary("/usr/lib/libllama.so", null);
                Console.WriteLine($"[Jarvis] Geselecteerd via Arch Linux systeem-bibliotheek (/usr/lib/libllama.so).");
            }
            else
            {
                NativeLibraryConfig.Instance.WithLibrary(prefix == "lib" ? "libllama.so" : "llama.dll", null);
                Console.WriteLine($"[Jarvis] Lokale CPU binaries niet gevonden, gebruik gemaakt van standaard-fallback.");
            }
        }
    }

    public static ModelParams CreateOptimizedParameters(IConfiguration configuration)
    {
        Configure(configuration);

        var modelPath = configuration["Jarvis:ModelPath"] ??
                        throw new ArgumentNullException("ModelPath is niet ingesteld in appsettings.json");
        var contextSize = uint.TryParse(configuration["Jarvis:ContextSize"], out var size) ? size : 3072;

        var cpuCores = Environment.ProcessorCount / 2;
        if (cpuCores <= 0) cpuCores = 4;

        return new ModelParams(modelPath)
        {
            SplitMode = GPUSplitMode.None,
            ContextSize = contextSize,
            Threads = cpuCores,
            GpuLayerCount = _useGpu 
                ? int.TryParse(configuration["Jarvis:GpuLayerCount"], out var gpuLayerCount) 
                    ? gpuLayerCount : 99 // 99 stuurt vrijwel zeker alle lagen van een 8B model door
                : 0,
        };
    }
}