# Potato: An Interactive CLI for GLaDOS OpenAI-Compatible APIs

Potato is an interactive command-line client designed to interface with GLaDOS — an OpenAI-compatible API server. It enables users to perform structured, step-by-step tasks on local code repositories with explicit approval workflows and tool-based execution. Potato is built to be conservative in system access, requiring user confirmation before executing potentially destructive or system-changing operations.

This project is not part of the "Rewrite" project — it is a standalone, self-contained CLI tool designed for local development and code manipulation using GLaDOS.

---

## 📦 Installation & Setup

### Prerequisites

- .NET 6.0 or later
- A GLaDOS-compatible server (e.g., [Ollama](https://ollama.com/) running locally or remotely)
- Terminal access (supports Unix-like systems)

### Running Potato

Clone or navigate to the project directory:

```bash
cd /path/to/Potato
```

Run the CLI using:

```bash
dotnet run --project Potato
```

### ACP mode

Run Potato as an [Agent Client Protocol](https://agentclientprotocol.com/) agent over stdin/stdout:

```bash
dotnet run --project Potato -- --acp --model <model-id>
```

ACP mode uses newline-delimited JSON-RPC on stdout. It supports ACP v1 `initialize`, `session/new`, `session/prompt`, `session/cancel`, and `session/close`; output is sent as `session/update` notifications and mirrored in the GLaDOS Agents view. Prompts run through Potato's ReAct loop, including local edit and shell tools. Before those tools run, Potato sends a `session/request_permission` request so the connected IDE—not the GLaDOS web UI—shows the approval choices.

### Configuring the Endpoint

By default, Potato connects to:

```
http://localhost:11434/v1
```

To override this endpoint, set the `GLaDOS_OPENAI_ENDPOINT` environment variable:

```bash
GLaDOS_OPENAI_ENDPOINT=http://your-server:port dotnet run --project Potato
```

---

## 🧠 How Potato Works

Potato operates in a **staged review loop** to ensure safety, clarity, and control:

1. **Proof-carrying plan** — The agent proposes a bounded plan and asks for approval before execution. Each step includes its action, evidence needed, expected result, verification method, and rollback guidance.
2. **Execution** — Potato runs a bounded ReAct loop to inspect files, execute commands, apply patches, and collect tool observations.
3. **Evidence record** — The final result includes recent observed evidence and states whether verification was collected after a file change. Individual edits and commands still require their own permission.

---

## 🛠️ Supported Actions

Potato supports a wide range of code and project manipulation tasks, including:

- ✅ **Code Review** — Analyze code, suggest improvements, or report issues.
- ✅ **File Creation** — Create new files with specified content.
- ✅ **File Editing** — Apply patches or write code into existing files.
- ✅ **Project Inspection** — Analyze the project structure and files.
- ✅ **Documentation Writing** — Generate or update documentation.
- ✅ **Refactoring** — Architectural changes and code restructuring.
- ✅ **Shell Command Execution** — Run system commands with explicit user approval.
- ✅ **ReAct Execution** — Uses one tool at a time, incorporating each observation before continuing.

---

## 🧩 Tools & Commands

Potato exposes direct tools to the ReAct loop. Each tool has a unique name and is invoked as needed.

### Built-in Tools

- `ReadFileTask` — Reads a file and returns its content.
- `ReadFileRange` — Reads a bounded line range from a text file.
- `WriteCodeTask` — Writes code to a file.
- `ApplyPatchTask` — Applies a patch to a file.
- `InspectProjectTask` — Analyzes the project structure.
- `CreateNewFileTask` — Creates a new file with specified content.
- `CodeReviewTask` — Reviews code for quality or bugs.
- `WriteDocumentationTask` — Generates or updates documentation.
- `WriteReportTask` — Generates a summary report.

### Shell Commands

Potato can execute shell commands if explicitly requested and approved by the user.

> ⚠️ Shell commands are shown to the user and require explicit approval before execution.

Example:

```text
> shell: git status
```

The CLI will prompt:

```text
Execute shell command? [y/N]: y
```

---

## ✅ Approval Workflow

Potato uses a **dual approval system** for safety:

### 1. Proof-plan approval

Before execution, Potato displays the proof-carrying plan and asks for approval.

**Approvals accepted:**

```
y, yes, approved, approve, go, ok, okay, correct
```

> If you type `yes`, Potato approves the plan and begins evidence-gathering execution. Edits and shell commands still require their own permission.

### Checkpoint rollback

Every successful Potato file write creates an in-memory checkpoint containing the
previous file contents. Use `/checkpoints` to inspect them and `/rollback` to
restore the latest one; use `/rollback <number>` to select a listed checkpoint.
Potato asks for confirmation and refuses to overwrite a file that changed after
the checkpoint was created. Checkpoints expire when Potato exits.

Completed agent tasks also get one combined checkpoint. Use `/task-checkpoints`
and `/rollback-task [number]` to restore every file touched by a completed task
to its state before that task began.

### 2. Execution Approval

For risky or multi-step tasks, Potato will ask for explicit approval before executing.

**Approvals accepted:**

```
execute, run, do it, continue, proceed, go
```

> Type `exit` or `quit` to close the CLI.

---

## 📄 Prompt Library

Potato uses a modular prompt system stored in the `Prompts/` directory. Each prompt file corresponds to a task type:

- `GreetingPrompts.cs` — For initial user greeting.
- `ReActPrompts.cs` — Guides direct tool-driven execution.
- `ProjectMapPrompts.cs` — Summarizes files for project-map indexing.
- `SideQuestionPrompts.cs` — Handles questions outside the active task.

These prompts are designed to guide the agent’s behavior and ensure consistent, safe execution.

---

## 📁 Project Structure

```
Potato/
├── Models/                   # Core model definitions
├── Prompts/                  # Prompt templates for each task type
├── Session/                  # Session management and execution logic
├── Tools/                    # Tool registry and execution logic
├── CurrentChatClientState.cs # Manages chat client state
├── Potato.csproj            # .NET project file
├── PotatoAppSettings.cs     # Application settings
├── PotatoConsole.cs         # Main console logic
├── Program.cs               # Entry point
├── FEATURE.md               # Feature overview and design
└── README.md                # This document
```

---

## 🧪 Testing & Development

Potato is designed to be testable and extensible. The `Session/` directory contains the core execution logic, including:

- `PotatoSession.cs` — Manages interactive sessions.
- `PlanningService.cs` — Builds direct-execution guidance and project-map context.

Testing is done via unit tests in the `Tests/` directory (not currently visible in the scan).

---

## 📝 Example Usage

### 1. Inspect Project

```text
> inspect project
```

Potato will generate a project map and ask for approval.

### 2. Write Code

```text
> write code /src/HelloWorld.cs
```

Potato will generate code, ask for approval, then execute.

### 3. Apply Patch

```text
> apply patch /src/HelloWorld.cs
```

Potato will generate a patch, ask for approval, then apply.

### 4. Shell Command

```text
> shell: ls -la
```

Potato will show the command and ask for approval before execution.

---

## 📌 Shortcuts & Help

Type `?` to see available shortcuts.

- `Up` — Recall older commands
- `Down` — Move back toward current draft
- `?` — Show shortcuts
- `exit` — Close the CLI

---

## 🚫 Limitations & Safety

Potato is designed to be **conservative**. It:

- Does not auto-execute shell commands.
- Requires explicit approval before writing, deleting, or installing.
- Does not run tools or emit tool-call JSON unless approved.
- Uses in-memory history of commands to avoid losing context.

---

## 📦 Dependencies

Potato uses .NET 6.0 and relies on the following NuGet packages:

- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging`
- `Newtonsoft.Json` (for JSON handling)
- `System.Text.Json`

No external dependencies beyond GLaDOS are required.

---

## 📚 License

This project is licensed under the MIT License. See the `LICENSE` file for details.

---

## 🙋‍♂️ Need Help?

If you encounter issues or have feature requests, please open an issue on the GitHub repository or contact the maintainers.

---

## 📝 Report for Further Improvement

To improve this README.md further, I would recommend:

1. **Add Screenshots or CLI Examples** — Visual examples of the CLI in action would help users understand the workflow.
2. **Add a “Getting Started” Section** — A step-by-step guide for new users to set up and run Potato.
3. **Add Versioning & Release Notes** — Information on releases, changelogs, and versioning.
4. **Add Contribution Guidelines** — Instructions for developers who want to contribute.
5. **Add API Documentation** — If Potato exposes any APIs or tool interfaces.
6. **Add Testing Instructions** — How to run tests locally.
7. **Add Known Issues** — Document known bugs or limitations.
8. **Add FAQ** — Common questions and answers for users.

Would you like me to generate a report with these suggestions formatted for inclusion in the README.md?
