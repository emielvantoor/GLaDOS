**Prompt:**  
"Build the `GLaDOS/GLaDOS.csproj` project with the following optimized arguments:
```bash
dotnet build GLaDOS/GLaDOS.csproj --no-restore /p:AllowMissingPrunePackageData=true /p:NuGetAudit=false -m:1 -v:minimal
```  
**Purpose:**
- `--no-restore`: Skips NuGet package restoration (useful if dependencies are already restored).
- `/p:AllowMissingPrunePackageData=true`: Bypasses missing package data pruning checks.
- `/p:NuGetAudit=false`: Disables NuGet audit for faster builds.
- `-m:1`: Limits build to a single machine (for parallelism control).
- `-v:minimal`: Reduces build output verbosity.


GLaDOS v2 – Agent Protocol Architecture
Doel

GLaDOS moet geen AI-agent zijn die zelf alle tool-protocollen begrijpt.

GLaDOS moet een Agent Orchestrator worden die meerdere AI-agent protocollen kan hosten, routeren en combineren.

Het LanguageModel mag volledig protocol-onafhankelijk worden.

Architectuur
Browser / Rider / VSCode
│
▼
GLaDOS.Agent (Orchestrator)
│
┌────────────────┼────────────────┐
▼                ▼                ▼
GLaDOSProtocol    QwenProtocol    OpenAIProtocol
│                │                │
└────────────────┼────────────────┘
▼
Tool Router
┌────────────┐
│            │
Internal Tools   External Agent
│            │
▼            ▼
GLaDOS Tools    Rider/QwenAgent
│
▼
LanguageModel
│
▼
llama.cpp
Design Principles
1. LanguageModel moet dom zijn

De LanguageModel is uitsluitend verantwoordelijk voor:

model laden
prompt versturen
tokens ontvangen
complete response teruggeven

De LanguageModel weet niets over:

tool calls
ChatML
OpenAI
Qwen
Claude
XML
JSON
planning

Hij is slechts een transportlaag.

2. Protocols bevatten alle promptlogica

Maak een interface:

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

Iedere provider krijgt zijn eigen implementatie.

Voorbeelden:

Protocols/

GLaDOSProtocol.cs

OpenAIProtocol.cs

QwenProtocol.cs

ClaudeProtocol.cs

JetBrainsProtocol.cs
3. ToolParser hoort bij een protocol

Niet het model bepaalt hoe een tool-call eruit ziet.

Maar het protocol.

Voorbeeld:

Qwen:

<think>

[tool_call: read_file ...]


OpenAI:

{
"name":"read_file",
"arguments":{}
}

Claude:

<tool_call>
...
</tool_call>

Wordt intern:

AgentToolCall
{
Provider = "Qwen",
ToolName = "read_file",
Arguments = ...
}
4. Eén intern ToolCall model

Gebruik één universeel model.

public class AgentToolCall
{
public string Provider;

    public string ToolName;

    public JsonNode? Arguments;

    public string RawCall;
}

Hiermee hoeft de rest van GLaDOS nooit meer te weten welk protocol gebruikt werd.

5. ToolRouter

Maak een centrale ToolRouter.

AgentToolCall

↓

Internal Tool ?

YES

↓

Execute

↓

ToolResult

NO

↓

External Agent

↓

Forward

De ToolRouter beslist uitsluitend:

intern uitvoeren
extern routeren
6. GLaDOSAgent wordt een Orchestrator

GLaDOSAgent bevat geen protocolcode.

Zijn taak wordt:

Protocol.BuildPrompt()

↓

LanguageModel

↓

Protocol.Parse()

↓

ToolRouter

↓

Execute

↓

Protocol.BuildToolResponse()

↓

LanguageModel

GLaDOSAgent weet dus niet meer hoe Qwen tool-calls eruit zien.

7. Internal vs External Tools

Internal tools:

Clock

Weather

Filesystem

Home Automation

Calendar

External tools:

Rider

QwenAgent

Cursor

VSCode

Claude Desktop

Een externe tool wordt nooit uitgevoerd.

Alleen doorgestuurd.

8. Thinking is protocol specifiek

Qwen:

<think>

...

Claude:

thinking

OpenAI:

geen thinking

Daarom hoort thinking stripping in het protocol.

Niet in LanguageModel.

9. Prompt formatting hoort niet in LanguageModel

Verplaats:

FormatHistoryToChatML()

naar

QwenProtocol.BuildPrompt()

Later:

OpenAIProtocol.BuildPrompt()

ClaudeProtocol.BuildPrompt()

JetBrainsProtocol.BuildPrompt()
10. Tool Results

Het protocol bepaalt ook hoe een tool-resultaat teruggestuurd wordt.

Qwen:

<tool_response>

...

OpenAI:

role=tool

Claude:

eigen formaat

Dus:

Protocol.BuildToolResponse()
Directory structuur
GLaDOS.Core

    Protocols/

        IAgentProtocol.cs

        OpenAIProtocol.cs

        ClaudeProtocol.cs

        QwenProtocol.cs

        GLaDOSProtocol.cs

    Routing/

        ToolRouter.cs

        ToolRegistry.cs

    Models/

        AgentToolCall.cs

        AgentToolResult.cs

    Agents/

        GLaDOSAgent.cs

GLaDOS.LLama

    LLamaLanguageModel.cs

    LLamaModelLoader.cs


When running dotnet build use the following arguments:
dotnet build GLaDOS/GLaDOS.csproj --no-restore /p:AllowMissingPrunePackageData=true /p:NuGetAudit=false -m:1 -v:minimal