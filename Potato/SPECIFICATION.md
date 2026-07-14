# Potato Specification Notes

This document captures the current state of Potato and the direction that looks best for a local agent running against a local model.

## 1. Current State

Potato is currently a staged CLI agent with:

- model selection from a GLaDOS-compatible endpoint
- a planning phase that produces deterministic task lists
- task execution through `AgentTask` implementations
- a separate ReAct execution mode that can call tools directly
- local safety gates for shell commands and file edits

## 1.5 Architecture Style

Potato should follow SOLID boundaries and a clean-architecture style separation of concerns:

- runtime owns execution
- planning and prompts support the runtime
- UI is only a surface
- dependency direction should stay inward
- tools are the boundary to external actions

## 2.5 Personality and Tone

- GLaDOS is the overall theme and voice.
- Potato is the constrained local runtime running on limited resources.
- The local model is the execution brain, not the persona owner.
- The personality may be witty or GLaDOS-like, but it must never override safety, tool routing, or execution rules.

## 2. Current Product Features

Potato currently also has these user-facing features:

- model selection from `/v1/models`
- slash commands for `/model`, `/cd`, `/ask`, `/prompts`, `/mode`, `/sessions`, `/transcript`, `/continue`, and `/abort`
- file mention expansion with `@path/to/file`
- session tracking and transcript saving
- prompt files loaded from disk or compiled defaults
- execution mode persistence in app settings
- startup banner and greeting
- Web UI reporting for sessions and model activity
- project-map indexing and targeted project-map search
- local tool execution for read, search, patch, create, review, and shell work

Prompt files are user-improvable even in a compiled build:

- compiled defaults provide the built-in baseline
- external prompt files can override or refine the defaults at runtime
- missing files may be created from the compiled defaults so users can edit them
- `/prompts` switches between compiled-default and external-file prompt modes

## 3. Potentially Wrong or Reconsider

Some current features may be the wrong shape for the long-term design:

- the `pipeline` planner/task stack is likely too heavy for local models
- `AgentTask` is probably too much abstraction for actual edits and commands
- the full project-map cache validation path is too expensive if it runs on the hot path
- the Web UI connection should stay passive unless it shares the same orchestration rules
- dual prompt sources (`compiled defaults` and external files) are useful, but they increase drift risk if they diverge
- the current split between planning, task generation, and execution may be harder to reason about than direct tool-driven ReAct

These features are not necessarily bad, but they should be treated as candidates for simplification rather than core architecture.

### Latest direction

- Make ReAct + direct tools the center.
- Keep `pipeline` only as a fallback until the direct path is stable.
- Keep `AgentTask` only as thin wrappers around one clear workflow step.
- Keep Web UI passive and informational.
- Keep planning as a helper that produces compact step guidance, not a separate agent personality.
- If a feature makes the model think twice about the same action, it is probably too much.

### 7B model guidance

- Keep prompts small and phase-specific.
- Keep tool schemas strict and deterministic.
- Keep each step to one action and one observation.
- Add explicit fallback behavior when the model fails to choose a tool.
- Validate outputs aggressively instead of relying on model judgment.

## 4. Main Problem

The current `AgentTask` layer is doing too much.

It is not just a task router. In several cases it asks the model to generate the actual edit, patch, or command content again. That creates fragile handoffs:

- one task succeeds, the next fails because the task lost exact file state
- prompts drift across steps
- the model has to re-derive context that the runtime already knows
- local models are more likely to degrade across chained task abstractions

## 5. Recommendation

Use direct tool calls as the primary execution path.

Keep `AgentTask` only for high-level workflow orchestration and guardrails, not for low-level action generation.

### Recommended split

- **Model**
  - chooses the next action
  - emits tool calls
  - returns final answers

- **Runtime**
  - validates permissions
  - executes tools
  - stores observations
  - keeps step state

- **AgentTask**
  - defines allowed workflow shapes
  - adds constraints and guidance
  - does not own the actual content of edits or shell commands

## 6. Target Architecture

### 6.1 Primary mode

ReAct-style direct tool calling should be the default execution model.

The model should call low-level tools such as:

- read file
- list files
- search file contents
- apply patch
- create file
- run shell command

### 6.2 Task layer

`AgentTask` should become a thin layer around stable workflow types, for example:

- inspect
- read
- patch
- create
- shell
- review

Each task should be deterministic and narrow.

### 6.3 Planning layer

Planning should only produce:

- ordered steps
- constraints
- required inputs
- expected result for each step

It should not try to be an extra execution engine.

## 7. Prompt Rules

Prompts should be small and role-specific.

### Specification prompt

Should only answer:

- what will be done
- what must be inspected first
- what is not yet known

It should not invent repository facts.

### Execution prompt

Should only answer:

- next step
- current observation
- exact tool to use next

### Edit prompt

Should always include:

- exact file path
- exact target content or exact change target
- exact reason for the change

## 8. PromptLibrary Guidance

`PromptLibrary` should be a thin prompt catalog, not a workflow engine.

### Keep it doing

- storing one prompt per phase or job
- separating system prompts from user prompts
- versioning prompt text when behavior changes
- sharing only small reusable fragments when rules repeat

### Avoid

- giant prompts that try to encode the whole architecture
- duplicated rules across many prompts
- hidden orchestration inside prompt text
- prompts that know too much about other phases

The safest pattern is:

1. system prompt = role and boundaries
2. user prompt = current task context
3. prompts stay short and phase-specific
4. each prompt owns only one job

## 9. Tool Surface Guidance

The tool set should stay small and primitive.

### Keep

- read, list, search
- create, edit, patch
- shell
- context retrieval
- targeted project-map search

### Avoid adding

- high-level wrapper tools that only re-prompt the model
- duplicate inspection tools with overlapping behavior
- planner-like actions disguised as tools
- tools that exist only to compensate for prompt complexity

The model should call tools directly for real work. Planning and `AgentTask` logic should remain workflow layers, not part of the core tool surface.

## 10. Local Model Guidance

Local models usually work better when:

- each step is short
- the runtime enforces structure
- prompts do not depend on long hidden context
- the model does not have to synthesize large edits from multiple abstractions

So the safest approach is:

1. inspect first
2. act with one tool
3. observe result
4. continue

## 11. Planning Cache Guidance

The ProjectMap should stay, but it should behave like a lazy search index rather than something the planner drags into every request.

### What to avoid

- do not send the full ProjectMap with each planning request
- do not make cache validation part of the hot path if it requires scanning and hashing the whole tree
- do not treat indexing as required before every user action

### Better approach

- keep the planning context compact
- use a short header plus targeted search hits
- refresh or validate the cache out of band when possible
- prefer timestamps and size checks over repeated full hashing
- use `search-project-map` first, then exact file reads for the returned paths

### Practical rule

If the planner already knows the current folder and has a small search result set, that is enough. The cache should help discovery, not dominate every turn.

## 12. UI Connection Guidance

The GLaDOS main UI connection should be treated as a transport/status integration, not as the source of agent behavior.

The CLI should still own:

- execution policy
- tool routing
- step state
- permission handling

The UI should not become a second orchestration layer unless the same protocol rules are shared.

## 13. UI Safety

The UI must not be allowed to interfere silently with an active CLI session.

- Default mode should be observe-only.
- Any UI-driven input must require explicit opt-in or a session lock.
- Remote UI input must never preempt local CLI input without the user choosing that mode.
- Each Potato session should be isolated with its own session identity.
- The CLI remains the authority for execution and permission decisions.

## 14. UI Motivation

The UI is also the main ergonomic answer to the CLI-only problem.

- The CLI is fine for short commands, but it is weak for multiline input.
- The UI should make longer prompts and follow-up context easier to enter.
- This is the same reason tools like CopilotCli or QwenCode feel better when they support richer input.
- Even with a better UI, execution rules and safety still belong in the runtime, not the interface.

## 15. Success Criteria

This direction is good if:

- one successful action reliably leads to the next
- the model no longer needs to re-invent edits
- local model runs are easier to predict
- the prompt stack becomes shorter and simpler
- failures are local and recoverable

## 16. Suggested Next Implementation Direction

1. Reduce `AgentTask` responsibility to workflow definition only.
2. Move low-level action selection into direct tool calling.
3. Keep `ReAct` as the main execution loop.
4. Preserve approvals and safety boundaries.
5. Make the UI connection passive unless it shares the same tool protocol.

## 17. Final Implementation Advice

- Keep `pipeline` only as a fallback, and prefer deleting it later if ReAct stays stable.
- Keep `AgentTask` thin; if it needs to invent content again, it is too heavy.
- Keep planning compact and helper-like, not a second agent personality.
- Keep ProjectMap lazy and cheap; search first, validate later.
- Keep the UI observe-only by default for safety.
- Keep prompts editable through external files even in a compiled build.
- Optimize for a 7B model by being deterministic, explicit, and small-step.
