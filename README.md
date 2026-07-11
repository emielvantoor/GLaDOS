# GLaDOS Project

## Overview

GLaDOS is an advanced, modular AI platform designed to host, orchestrate, and combine multiple AI agent protocols. It functions as an **Agent Orchestrator**, enabling protocol-agnostic interaction with language models while abstracting away tool-call complexities. The platform is built on a scalable .NET architecture with a focus on extensibility, allowing developers to plug in new protocols, tools, and agents without modifying core logic.

GLaDOS is not a monolithic AI agent but a flexible infrastructure for managing agent systems. It supports multiple protocols (OpenAI, Qwen, Claude, JetBrains, etc.), internal tools (file system, clock, calendar), and external agents (Rider, Cursor, VSCode). The system is designed to be protocol-agnostic, meaning the language model itself is decoupled from protocol-specific logic — making it easy to migrate or add new protocols.

## Key Features

- **Protocol-Agnostic Agent System**: Host and route multiple AI agent protocols (OpenAI, Qwen, Claude, JetBrains, etc.) without modifying core code.
- **Modular Tool Router**: Internal tools (file system, weather, clock) are executed locally; external tools (Rider, Cursor) are forwarded to their respective agents.
- **Language Model Abstraction**: The model is limited to loading, sending prompts, receiving tokens, and returning responses — it does not handle tool calls, prompt formatting, or thinking logic.
- **Web Interface**: ASP.NET Core web app with rich UI components for real-time chat, tool execution, and model management.
- **Tool Integration**: Built-in JavaScript tools for bash execution and web search, plus extensible tool registry.
- **ONNX & Llama Support**: Models can be loaded and executed using ONNX or GGUF (Llama-compatible) formats.
- **Extensible Architecture**: New protocols and tools can be added by implementing interfaces and registering them in the orchestrator.
- **Rewrite Protocol Support**: Experimental natural language to shell command translation capability (see [Rewrite/FEATURE.md](Rewrite/FEATURE.md) for details).

## Project Structure

```
GLaDOS/              # Main web application
├── Models/          # Chat completion models and responses
├── Endpoints/      # API endpoints for chat operations
├── Converters/     # Data format conversion utilities
├── wwwroot/        # Static files (CSS/JS) for web interface
└── Program.cs      # Entry point

GLaDOS.Core/        # Core logic and services
├── Agents/         # Agent message/response interfaces
├── Bootstrapper/   # Service registration configuration
└── Interfaces/     # Core abstractions

GLaDOS.LLama/       # Llama model integration
├── ModelLoaders/   # GGUF model loading utilities
├── InferencePipelines/ # Llama inference pipelines
└── Utilities/      # Llama-specific utilities

GLaDOS.Onnx/        # ONNX model support
├── ModelLoaders/   # ONNX model loading utilities
├── InferenceEngines/ # ONNX inference engines
└── Converters/     # ONNX-to-LLaMA format conversion utilities

Potato/            # AI agent utilities for extensible agent systems and tool integration
├── ToolRegistry/   # Internal tool registry
├── AgentUtilities/ # Agent metadata and permission utilities
└── ExecutionEngine/ # Tool execution engine

Rewrite/           # Experimental agent protocol implementation
└── Rewrite.csproj  # Implementation for natural language to shell command translation (see FEATURE.md)

agents.md          # Agent system documentation
.gitignore         # Version control exclusions
GLaDOS.sln         # Solution file for multi-project management
```

## Getting Started

### Prerequisites

- [.NET SDK v8.0.100](https://dotnet.microsoft.com/download/dotnet/8.0) (minimum version required)
- Git installed for cloning the repository
- A compatible GPU or CPU for model inference (if using Llama or ONNX models)

### Clone the Repository

```bash
git clone https://github.com/yourusername/jarvis.git
cd jarvis
```

### Restore Dependencies

```bash
dotnet restore
```

### Build the Project

```bash
dotnet build GLaDOS/GLaDOS.csproj --no-restore /p:AllowMissingPrunePackageData=true /p:NuGetAudit=false -m:1 -v:minimal
```

> ⚠️ This build command requires .NET SDK v8.0.100 with the following dependencies:
> - Microsoft.AspNetCore.App 8.0.0
> - System.Text.Json 8.0.0

### Run the Application

```bash
dotnet run --project GLaDOS
```

The app will start on `http://localhost:5000` by default. Open your browser to access the web interface.

### Explore Documentation

- See [agents.md](agents.md) for details on the agent protocol architecture.
- Refer to the `GLaDOS/Program.cs` for entry point customization.
- Review the `wwwroot/js/tools/` directory for tool implementation examples.
- See [Rewrite/FEATURE.md](Rewrite/FEATURE.md) for information on the Rewrite protocol's natural language to shell command translation feature.

## Configuration

### Model Path

GLaDOS uses appsettings to define the model directory path. Edit the `appsettings.Development.json` or `appsettings.json` file:

```json
{
  "ModelPath": "/path/to/models"
}
```

### Supported Model Formats

- **Llama Models**: Only **GGUF** format is currently supported.
- **ONNX Models**: Supported through the `GLaDOS.Onnx` directory.
- **Qwen Models**: Supported via the `GLaDOS.LLama` directory with Qwen-compatible GGUF models.

> ⚠️ **Note**: The platform currently does not support OpenAI API models for inference (only for protocol simulation). For real-time inference, use Llama or ONNX models.

## Agent Protocol Architecture

GLaDOS is designed around a protocol-agnostic agent system. The core architecture is defined by the following principles:

### 1. Language Model as Transport Layer

The language model is responsible for:

- Loading models
- Sending prompts
- Receiving tokens
- Returning complete responses

It does **not** handle:

- Tool calls
- Prompt formatting (ChatML, JSON, XML)
- Thinking logic
- Protocol-specific parsing

This ensures the model remains protocol-independent and can be swapped or upgraded without changing the orchestration logic.

### 2. Protocol Interface

All protocols must implement the `IAgentProtocol` interface:

```csharp
public interface IAgentProtocol
{
    string Name { get; }

    string BuildPrompt(
        List<AgentMessage> history,
        IReadOnlyList<AgentToolDefinition> tools);

    IEnumerable<AgentToolCall> ParseResponse(string response);

    string BuildToolResponse(
        AgentToolCall toolCall,
        string toolResult);

    bool SupportsThinking { get; }
}
```

Examples include:

- `GLaDOSProtocol.cs`
- `OpenAIProtocol.cs`
- `QwenProtocol.cs`
- `ClaudeProtocol.cs`
- `JetBrainsProtocol.cs`
- `RewriteProtocol.cs` (experimental, see Rewrite/FEATURE.md)

### 3. Tool Router

The `ToolRouter` is responsible for deciding whether a tool call should be executed internally or forwarded to an external agent.

```csharp
if (toolCall.Provider == "Internal")
{
    ExecuteInternalTool(toolCall);
}
else
{
    ForwardToExternalAgent(toolCall);
}
```

### 4. Internal vs External Tools

**Internal Tools** (executed locally):

- Clock
- Weather
- Filesystem
- Calendar

**External Tools** (forwarded to external agents):

- Rider
- Cursor
- VSCode
- Claude Desktop

External tools are **never executed** — they are simply forwarded to their respective agent.

### 5. Thinking Logic

Thinking is protocol-specific and handled in the protocol implementation. For example:

- **Qwen**: `thinking` block in the response.
- **Claude**: `thinking` tag.
- **OpenAI**: No thinking support.

The `ParseResponse` method in each protocol is responsible for stripping or preserving thinking logic.

### 6. Rewrite Protocol

The Rewrite protocol provides experimental natural language to shell command translation. It operates as follows:

1. **Understanding Check**: Rewrite will first ask GLaDOS (an OpenAI-compatible AI model host) if it can understand the user's question (yes/no).
2. **Command Generation**: If GLaDOS confirms understanding, Rewrite will prompt the user to provide the specific shell command they want to generate.
3. **Error Handling**: For ambiguous queries, the system will return an error message indicating the ambiguity and suggest clarifying the request.

Example:
**Input:** "I would like to get a list of all files where the content contains Hi Emiel"
**Output:** `grep -rl "Hi Emiel" .`

This feature enhances usability by bridging the gap between natural language and command-line interfaces.

## Tool Integration

The web interface includes built-in JavaScript tools for:

- Executing bash commands (`execute-bash.js`)
- Performing web searches (`web-search.js`)

These tools are registered in the `wwwroot/js/tools/registry.js` file. You can extend the tool registry by adding new tool definitions and implementing their logic.

### Example: Adding a New Tool

1. Add a new tool file in `wwwroot/js/tools/` (e.g., `my-tool.js`).
2. Register it in `registry.js`:

```javascript
tools.push({
    name: "my-tool",
    description: "Description of the tool",
    execute: function (args) {
        // Tool logic here
    }
});
```

3. Update the agent protocol to support the tool.

## Contribution Guidelines

### 1. Review Existing Documentation

- Read [agents.md](agents.md) to understand the architecture.
- Review the `GLaDOS.Core/Agents/` and `GLaDOS.LLama/` directories for implementation patterns.

### 2. Follow .NET Coding Standards

- Use C# 10+ syntax.
- Follow .NET naming conventions.
- Use async/await for I/O operations.

### 3. Add Tests

- Write unit tests for new features using xUnit or NUnit.
- Test protocol implementations and tool routers.

### 4. Update Documentation

- Always update `agents.md` or `README.md` when adding new protocols or tools.
- Document any breaking changes or deprecated features.

### 5. Submit Pull Requests

- Submit PRs to `main` or `develop` branches.
- Include a clear description of the changes.
- Reference any relevant issues or documentation.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Notes

- The web interface includes JavaScript tools for bash execution and web search.
- ONNX model support is implemented in the `GLaDOS.Onnx` directory.
- Llama integration handles model loading and inference pipelines.
- The `Potato/` directory contains utility functions for common operations.
- The `Rewrite/` directory is **experimental** and contains the natural language to shell command translation feature — see [Rewrite/FEATURE.md](Rewrite/FEATURE.md) for usage and implementation details.
- GLaDOS is designed for **multi-protocol agent orchestration**, not for direct AI agent execution.

## Future Improvements

- Add support for OpenAI API models for streaming and tool calls.
- Implement more external agent integrations (Cursor, VSCode, Claude Desktop).
- Add model quantization support for Llama and ONNX models.
- Add logging and monitoring for tool execution and agent performance.