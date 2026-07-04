# Potato

`Potato` is an interactive command-line client for the GLaDOS OpenAI-compatible API.

It connects to a GLaDOS server, lets the user choose a model, then runs a structured review loop before executing tasks. The CLI is intentionally conservative around local system access: shell commands are shown to the user and require explicit permission before execution.

## Startup

On startup the CLI:

1. Connects to the GLaDOS OpenAI-compatible endpoint.
2. Loads available models from `/v1/models`.
3. Prompts the user to choose a model by number or name.
4. Creates an OpenAI-compatible chat client for `/v1/chat/completions`.
5. Shows a Qwen-style terminal header with the selected model and current project folder.

The default endpoint is:

```text
http://localhost:11434/v1
```

Override it with:

```bash
GLaDOS_OPENAI_ENDPOINT=http://localhost:11434/v1 dotnet run --project Potato
```

## Review Loop

The CLI uses a staged workflow:

1. Specification
   The agent summarizes the user request in clear bullet points and asks for approval.

2. Adjustment
   This phase only runs if the user rejects or changes the specification. If the user approves the specification, this phase is skipped.

3. Approach
   After approval, the agent explains how the task will be completed. It names the available CLI tool or tools it intends to use and why. If no direct tool fits, it states whether the task can be solved through shell execution and what kind of shell action would be needed. It must not run tools, emit tool-call JSON, or print exact shell commands in this phase.

4. Execution
   The CLI executes the approved approach through available tools.

For simple read-only or inspection tasks, the CLI may proceed from the approach directly to the command permission prompt. For risky, destructive, write, install, delete, or multi-step tasks, the agent should ask the user to type `execute` before continuing.

## Approval Commands

Specification approval accepts short confirmations such as:

```text
y
yes
approved
approve
go
ok
okay
correct
```

Risky or multi-step execution can be confirmed with:

```text
execute
run
do it
continue
proceed
go
```

Type `exit` or `quit` to close the CLI.

Type `?` to show shortcuts.

Messages can include `@path/to/file` references. Potato resolves relative paths from the current project folder, reads the referenced text files, and appends their contents to the message sent to the model.

Supported examples:

```text
explain @Potato/Program.cs
review @"path with spaces/file.cs"
summarize @~/notes/context.md
```

## Slash Commands

Slash commands are handled by the CLI before a message is sent to the staged agent workflow.

- `/model`
  Shows the model selection prompt again and switches the active chat client to the selected model.

- `/cd [path]`
  Changes the CLI working directory. Relative paths are resolved from the current working directory, `~/` paths are expanded, and `file://` paths are supported.

- `/ask question`
  Sends a one-off side question to the selected model without adding the question or answer to the main staged conversation history.

- `/abort`
  Cancels the current staged task, clears the in-progress conversation history, and returns to the main prompt while keeping the selected model and working directory.

## Tools

The CLI exposes local tools to the agent:

- `GetCurrentTime`
  Returns the current local system date and time.

- `ReadFileContent`
  Reads a specific text file from disk.

- `ExecuteShellCommandAsync`
  Runs a shell command after showing the exact command to the user and asking for permission.

Tool names are generated from the C# method names in `AgentTools`, so prompt instructions stay aligned with the registered methods.

## Shell Execution

Shell execution is permissioned.

Before running a command, the CLI prints:

- the requested tool action
- the shell that will be used
- the working directory
- the exact command

The user must approve with:

```text
y
yes
```

If permission is denied, the tool returns a denial message to the agent and does not run the command.

Shell selection:

- Windows: `powershell.exe`
- Linux/macOS: `bash -lc`

Commands have a default timeout of 60 seconds and are capped at 600 seconds.

## Execution Planning

After the approach phase, the CLI can ask the selected GLaDOS model to produce an execution plan.

The execution planner returns JSON with:

```json
{
  "command": "command to run",
  "workingDirectory": null,
  "timeoutSeconds": 60
}
```

If the model returns a raw shell command instead of JSON, the CLI treats that text as the command and still routes it through the permissioned shell tool.

The planner is instructed to prefer read-only commands for inspection tasks and to avoid destructive commands unless the approved task explicitly requires them.

## Safety Boundaries

The CLI does not silently execute shell commands.

Even when a task is simple enough to skip the extra `execute` prompt, the shell tool still asks for command-level permission before running anything.

The extra `execute` prompt is reserved for tasks that appear risky, destructive, write-oriented, install-oriented, delete-oriented, or multi-step.

## Current Limitations

- Risk detection is heuristic and based on the approved specification and approach text.
- Execution planning depends on the selected model producing a useful command or JSON plan.
- The CLI-local tools are separate from GLaDOS server-side `IAgentTool` registrations.
- Long-running commands are killed when they exceed the configured timeout.
