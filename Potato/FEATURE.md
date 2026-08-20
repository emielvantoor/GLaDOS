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

1. Proof-carrying plan
   Before execution, Potato asks the selected model for a short structured plan. Each step contains an action, evidence that must be collected, an expected result, a verification method, and rollback guidance. Potato presents that plan for approval; this approval does not bypass later edit or command permissions.

2. Execution
   The CLI runs a bounded ReAct loop after plan approval. Tool observations are retained as execution evidence. The approved plan is included in the execution context so the model can use its expected result and verification method while it works.

3. Evidence record
   When the agent returns `FINAL:`, Potato appends an execution record with recent observed tool evidence and reports whether verification was collected after a file change. This is evidence for review, not a claim that an unrun verification succeeded.

For simple read-only or inspection tasks, the CLI may proceed from the execution steps directly to the command permission prompt. For write, delete, install, risky, or multi-step tasks, the agent should ask the user to type `execute` before continuing. Once execution is approved, the registered tools are allowed to perform the approved work.

The CLI owns the execution decision. If it does not auto-start after the execution steps, it prints an explicit status asking for `execute` or `yes`.

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

- `/sessions`
  Lists tracked sessions by number and subject. A new session is created from the first user request in a staged conversation, and completed or aborted sessions stay available until Potato exits.

- `/transcript`
  Shows the current session transcript. Use `/transcript <path>` to save the current session to a `.txt` file, `/transcript save <number> [path]` to save a tracked session, or `/transcript show <number>` to print a previous session.

- `/abort`
  Cancels the current staged task, clears the in-progress conversation history, and returns to the main prompt while keeping the selected model and working directory.

- `/checkpoints`
  Lists the reversible checkpoints that Potato created for successful file writes in the current process.

- `/rollback [latest|number]`
  Restores a Potato checkpoint after explicit confirmation. Rollback is refused if any affected file no longer matches the content Potato wrote, which protects later user edits.

- `/task-checkpoints`
  Lists one combined checkpoint for each completed Potato task that changed files.

- `/rollback-task [latest|number]`
  Restores all files touched by a completed task to their pre-task state after explicit confirmation.

## Tools

The CLI exposes local tools to the agent:

- `GetCurrentTime`
  Returns the current local system date and time.

- `ReadFileContent`
  Reads a specific text file from disk. Absolute paths are accepted; relative paths resolve from the current working directory.

- `ReadFileRange`
  Reads an inclusive line range from a specific text file without returning the whole file.

- `ApplyDiffPatchAsync`
  Applies a unified diff patch after showing the patch to the user and asking for permission. The tool validates the patch with `git apply --check` before applying it.

- `ExecuteShellCommandAsync`
  Runs a shell command after showing the exact command to the user and asking for permission.

Tool names are generated from the C# method names in `AgentTools`, so prompt instructions stay aligned with the registered methods.

Tool calls are printed with their parameters before execution. For file reads, Potato shows both the requested path and the resolved path.

During ReAct execution, Potato stores assistant responses and tool outputs as collected context. List entries include descriptors such as file paths, shell commands, or response previews, so smaller stateless models can choose the right index without relying on hidden memory or oversized prompts.

Potato retains the approved proof-carrying plan separately from collected context. ReAct memory stores the full observations used by the model, while the execution ledger records concise recent evidence and whether a verification command ran after a file change.

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

## Rollback checkpoints

Potato captures the complete pre-write contents of files changed by its create,
overwrite, search/replace, FIM, and unified-diff tools. A checkpoint can span
multiple files for a unified diff. `/rollback` restores the latest checkpoint;
`/rollback <number>` restores a selected entry from `/checkpoints`.

Checkpoints are in memory and expire when Potato exits. Before restoring,
Potato compares each current file to the content it wrote. If anything changed
in the meantime, rollback is refused rather than overwriting that later work.

In addition to individual write checkpoints, Potato merges all successful
writes in an approved ReAct run into a task checkpoint. It retains the first
pre-write version of each file and the final version Potato produced, so a task
rollback restores the entire completed task through one confirmed action. The GLaDOS Agents view
shows a rollback action for a completed task when Web UI input is enabled. That
action includes a `Changed Files` summary with scoped `+added -removed` line
counts for the files changed by the task.

## ReAct Execution Loop

After execution is approved, Potato runs a bounded observe-act loop:

1. Potato sends a compact next-action prompt containing the approved proof plan, original request, current working directory, and latest observation.
2. The first tool call in that ReAct iteration is executed through the local permissioned tools. Additional native tool calls in the same iteration are rejected before they run.
3. Tool results are treated as observations for the next iteration.
4. Potato records each tool result as evidence in the execution ledger.
5. The loop continues until the model responds with `FINAL:` or the iteration limit is reached.

The current loop limit is 24 iterations. Each assistant turn should either call the next useful tool or finish with `FINAL:`. Potato accepts `FINAL:` at the start of a response or on its own later line, so models that produce the answer first and append the marker still terminate the loop cleanly.

Some local models emit textual commands instead of native tool calls. When Potato sees a `<tool_call>{...}</tool_call>` block or a fenced shell command during the ReAct loop, it routes that action through the same permissioned local tool path and appends the result as the next observation.

For read-only project or folder inspection tasks, if the model fails to choose the first directory-listing action, Potato runs a deterministic read-only listing fallback through the same permissioned shell tool and continues with that observation.

## Execution planning

The proof-carrying planner asks the selected model for a small JSON plan before
execution. If the model returns invalid JSON or planning is unavailable, Potato
uses a conservative fallback plan: inspect relevant context, then make the
smallest justified change and verify it.

## Safety Boundaries

The CLI does not silently execute shell commands.

Even when a task is simple enough to skip the extra `execute` prompt, the shell tool still asks for command-level permission before running anything.

The extra `execute` prompt is reserved for tasks that appear write-oriented, delete-oriented, install-oriented, risky, or multi-step. Once execution is approved, the registered tools are allowed to perform the approved work.

## Current Limitations

- A plan is a reviewable contract, not a sandbox: individual tool permissions remain the enforcement boundary.
- ReAct execution depends on the selected model producing valid tool calls and a final `FINAL:` response.
- The CLI-local tools are separate from GLaDOS server-side `IAgentTool` registrations.
- Long-running commands are killed when they exceed the configured timeout.
- Potato session IDs are process-scoped. Sessions and rollback checkpoints are not persisted after either process exits.
