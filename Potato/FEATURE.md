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
   The agent summarizes the requested work in clear bullet points and asks for approval. It must not answer the task or invent repository facts during this phase. If the task requires local context, it states that execution must inspect the actual files first.

2. Adjustment
   This phase only runs if the user rejects or changes the specification. If the user approves the specification, this phase is skipped.

3. Approach
   After approval, the agent explains how the task will be completed. It names the available CLI tool or tools it intends to use and why. If no direct tool fits, it states whether the task can be solved through shell execution and what kind of shell action would be needed. It must not run tools, emit tool-call JSON, or print exact shell commands in this phase.

4. Execution
   The CLI executes the approved approach through a bounded ReAct loop. The model can inspect files, run commands, apply patches, observe results, and continue until it returns a final answer.

For simple read-only or inspection tasks, the CLI may proceed from the approach directly to the command permission prompt. For write, delete, install, risky, or multi-step tasks, the agent should ask the user to type `execute` before continuing. Once execution is approved, the registered tools are allowed to perform the approved work.

The CLI owns the execution decision. If it does not auto-start after the approach, it prints an explicit status asking for `execute` or `yes`.

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

Approval words are treated as standalone commands. A follow-up such as `yes what is PotatoConsole for?` is handled as a new request instead of approving an empty or previous workflow.

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

The prompt keeps an in-memory history of commands entered during the current session. Press `Up` to recall older commands and `Down` to move back toward the current draft.

Messages can include `@path/to/file` references. While typing an `@` path, matching folders and files are shown inline in gray. Use Left/Right to cycle suggestions and Enter to accept the current gray completion. Potato resolves relative paths from the current project folder, reads the referenced text files, and appends their contents to the message sent to the model.

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

- `/cd path`
  Changes the CLI working directory. While typing the path, matching directory names are shown inline in gray. Use Left/Right to cycle folder suggestions, press Enter once to accept the gray completion, then Enter again to run the completed command. Relative paths are resolved from the current working directory, `~/` paths are expanded, and `file://` paths are supported.

- `/ask question`
  Sends a one-off side question to the selected model without adding the question or answer to the main staged conversation history.

- `/abort`
  Cancels the current staged task, clears the in-progress conversation history, and returns to the main prompt while keeping the selected model and working directory.

## Tools

The CLI exposes local tools to the agent:

- `GetCurrentTime`
  Returns the current local system date and time.

- `ReadFileContent`
  Reads a specific text file from disk. Absolute paths are accepted; relative paths resolve from the current working directory.

- `GetCollectedContext`
  Retrieves context collected during the current ReAct execution. Use `index: "list"` to see stored items with contextual descriptions, `index: "latest"` for the newest item, or a numeric index to retrieve one item. Large entries are summarized automatically for compact retrieval; pass `full: true` only when exact content is needed.

- `ApplyDiffPatchAsync`
  Applies a unified diff patch after showing the patch to the user and asking for permission. The tool validates the patch with `git apply --check` before applying it.

- `ExecuteShellCommandAsync`
  Runs a shell command after showing the exact command to the user and asking for permission.

Tool names are generated from the C# method names in `AgentTools`, so prompt instructions stay aligned with the registered methods.

Tool calls are printed with their parameters before execution. For file reads, Potato shows both the requested path and the resolved path.

During ReAct execution, Potato stores assistant responses and tool outputs as collected context. List entries include descriptors such as file paths, shell commands, or response previews, so smaller stateless models can choose the right index without relying on hidden memory or oversized prompts.

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

## Diff Patching

Code edits should be made with unified diffs through `ApplyDiffPatchAsync`.

Before applying a patch, the CLI prints:

- the requested tool action
- the working directory
- the full patch

The user must approve with:

```text
y
yes
```

The patch is first checked with:

```bash
git apply --check --whitespace=nowarn
```

If validation succeeds, the CLI applies it with:

```bash
git apply --whitespace=nowarn
```

If either step fails, the tool returns the exit code, stdout, and stderr to the model as the next observation.

## ReAct Execution Loop

After execution is approved, Potato runs a bounded observe-act loop:

1. Potato sends a compact next-action prompt for the current step, including the original request, current working directory, and latest observation.
2. Tool calls are executed through the local permissioned tools.
3. Tool results are treated as observations for the next iteration.
4. The loop continues until the model responds with `FINAL:` or the iteration limit is reached.

The current loop limit is 12 iterations. Each assistant turn should either call the next useful tool or finish with `FINAL:`. Potato accepts `FINAL:` at the start of a response or on its own later line, so models that produce the answer first and append the marker still terminate the loop cleanly.

Some local models emit textual commands instead of native tool calls. When Potato sees a `<tool_call>{...}</tool_call>` block or a fenced shell command during the ReAct loop, it routes that action through the same permissioned local tool path and appends the result as the next observation.

For read-only project or folder inspection tasks, if the model fails to choose the first directory-listing action, Potato runs a deterministic read-only listing fallback through the same permissioned shell tool and continues with that observation.

## Execution Planning

Older versions of the CLI asked the selected GLaDOS model to produce a single execution plan after the approach phase.

The execution planner returns JSON with:

```json
{
  "command": "command to run",
  "workingDirectory": null,
  "timeoutSeconds": 60
}
```

The ReAct loop now handles approved execution instead, because coding tasks usually need multiple inspect, edit, and verify steps.

## Safety Boundaries

The CLI does not silently execute shell commands.

Even when a task is simple enough to skip the extra `execute` prompt, the shell tool still asks for command-level permission before running anything.

The extra `execute` prompt is reserved for tasks that appear write-oriented, delete-oriented, install-oriented, risky, or multi-step. Once execution is approved, the registered tools are allowed to perform the approved work.

## Current Limitations

- Risk detection is heuristic and based on the approved specification and approach text.
- ReAct execution depends on the selected model producing valid tool calls and a final `FINAL:` response.
- The CLI-local tools are separate from GLaDOS server-side `IAgentTool` registrations.
- Long-running commands are killed when they exceed the configured timeout.
