# GLaDOS Project

## Overview
GLaDOS is a multi-component AI platform featuring a web interface (GLaDOS), core logic (GLaDOS.Core), Llama integration (GLaDOS.LLama), ONNX model support (GLaDOS.Onnx), and utility tools (Potato). It provides chat functionality, model management, and extensible agent systems.

## Key Features
- **AI Chat Interface**: Web-based chat with real-time streaming and history
- **Model Management**: Support for OpenAI and custom Llama models
- **Extensible Agents**: Modular agent system with metadata and permissions
- **Web Interface**: ASP.NET Core web app with rich UI components
- **Tool Integration**: Built-in tools for bash execution and web search

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
1. **Prerequisites**: Install [.NET SDK](https://dotnet.microsoft.com/download)
2. **Clone Repository**: `git clone https://github.com/yourusername/jarvis.git`
3. **Restore Dependencies**: `dotnet restore`
4. **Run Application**: `dotnet run` from the solution root
5. **Explore Documentation**: See [agents.md](agents.md) for agent system details

## Contributing
- Review existing [agents.md](agents.md) documentation
- Follow .NET coding standards
- Add tests for new features (test directories may exist in subprojects)
- Update documentation when adding new features

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Notes
- The web interface includes JavaScript tools for bash execution and web search
- ONNX model support is implemented in the GLaDOS.Onnx directory
- Llama integration handles model loading and inference pipelines
- The Potato directory contains utility functions for common operations

For more information about the agent system, see [agents.md](agents.md).