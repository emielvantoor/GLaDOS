# GLaDOS Project

## Overview
GLaDOS is a comprehensive AI platform that integrates various components to provide advanced chat functionality, model management, and extensible agent systems. The platform is built with a modular architecture, allowing for easy expansion and customization. This README provides a detailed guide on setting up, building, and running the GLaDOS project.

## Key Features
- **AI Chat Interface**: Real-time chat with streaming capabilities and message history.
- **Model Management**: Support for OpenAI and custom Llama models.
- **Extensible Agents**: Modular agent system with metadata and permissions.
- **Web Interface**: ASP.NET Core-based web application with rich UI components.
- **Tool Integration**: Built-in tools for executing bash commands and performing web searches.

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

GLaDOS.Onnx/        # ONNX model support

Potato/            # AI agent utilities for extensible agent systems and tool integration

agents.md          # Agent system documentation
.gitignore         # Version control exclusions
GLaDOS.sln         # Solution file for multi-project management
```

## Getting Started
1. **Prerequisites**: Install [.NET SDK v8.0.100](https://dotnet.microsoft.com/download/dotnet/8.0) (minimum version required)
2. **Clone Repository**: 
   ```bash
   git clone https://github.com/yourusername/jarvis.git
   ```
3. **Restore Dependencies**: 
   ```bash
   dotnet restore
   ```
4. **Build Project**: 
   ```bash
   dotnet build GLaDOS/GLaDOS.csproj --no-restore /p:AllowMissingPrunePackageData=true /p:NuGetAudit=false -m:1 -v:minimal
   ```
   > ⚠️ This build command requires .NET SDK v8.0.100 with the following dependencies:
   > - Microsoft.AspNetCore.App 8.0.0
   > - System.Text.Json 8.0.0
5. **Run Application**: 
   ```bash
   dotnet run --project GLaDOS
   ```
6. **Explore Documentation**: See [agents.md](agents.md) for agent system details

## Configuration
- **Model Path**: The appsettings of GLaDOS contain a setting to specify the target path of where the models are located.
- **Format**: Only GGUF format is currently working with Qwen compatible models.

## Contributing
- Review existing [agents.md](agents.md) documentation
- Follow .NET coding standards
- Add tests for new features
- Update documentation when adding new features

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Notes
- The web interface includes JavaScript tools for bash execution and web search
- ONNX model support is implemented in the GLaDOS.Onnx directory
- Llama integration handles model loading and inference pipelines
- The Potato directory contains utility functions for common operations