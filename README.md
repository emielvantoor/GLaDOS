# GLaDOS

GLaDOS is an experimental local AI workspace. Its ASP.NET Core host loads one or more local GGUF models through LLamaSharp and provides an OpenAI-compatible API, a browser UI, model runtime information, tool execution, fill-in-the-middle completions, and Potato session endpoints.

This is a test and investigation project. Interfaces, configuration, and behavior may change without notice.

## Repository layout

- `GLaDOS/` — ASP.NET Core host, HTTP endpoints, and browser UI.
- `GLaDOS.Core/` — agent loop, prompt protocols, tool routing, and built-in tools.
- `GLaDOS.LLama/` — GGUF model loading and LLamaSharp hardware configuration.
- `GLaDOS.Onnx/` — ONNX-related project. It is included in the solution but is not currently referenced by the web host.
- `Potato/` — a separate console coding-agent client that connects to the local GLaDOS API.
- `Rewrite/` — a separate experimental command-translation console application.

## Requirements

- .NET SDK 10
- At least one local `.gguf` model file
- Suitable CPU or GPU resources for the selected model

## Configure and run

Set `GLaDOS:ModelPath` in `GLaDOS/appsettings.json` to either a `.gguf` file or a directory containing `.gguf` files. The checked-in value is a machine-specific example and should be changed before running the host.

```json
{
  "GLaDOS": {
    "ModelPath": "/path/to/models",
    "HardwareMode": "GPU",
    "GpuLayerCount": 99,
    "ContextSize": 64000
  }
}
```

Restore, build, and start the web host:

```bash
dotnet restore GLaDOS.sln
dotnet build GLaDOS.sln --no-restore
dotnet run --project GLaDOS
```

The host listens on `http://localhost:11434`. Its UI is available at `/index.html` and the OpenAI-compatible routes are under `/v1`, including `/v1/models` and `/v1/chat/completions`.

## Current capabilities

- Loads every top-level `.gguf` model in the configured model directory, or one configured `.gguf` file.
- Supports Qwen and GLaDOS prompt protocols in the host’s agent loop.
- Provides streaming chat completions, model discovery, tool endpoints, runtime-memory endpoints, and fill-in-the-middle completions.
- Includes built-in system-time and temperature tools.
- Provides Potato session endpoints for its separate local coding-agent client.

The web host currently uses the LLama integration; ONNX support is not enabled in `GLaDOS/GLaDOS.csproj`.

## Optional companion applications

Run Potato after the GLaDOS host is available:

```bash
dotnet run --project Potato -- --model <model-id>
```

Run Rewrite to translate a natural-language request into a shell command:

```bash
dotnet run --project Rewrite -- "list files containing Hello"
```

Rewrite connects to GLaDOS using its environment-based configuration. See [Rewrite/FEATURE.md](Rewrite/FEATURE.md) for its current design notes.

## License

Licensed under the [MIT License](LICENSE).
