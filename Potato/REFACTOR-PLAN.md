# Potato Refactor Plan

## Goal

Make Potato easier to reason about for local models by moving to a small, direct-tool execution core and shrinking the planner/task/prompt layers around it.

## Architecture Style

Potato should follow SOLID boundaries and a clean-architecture style separation of concerns:

- runtime owns execution
- planning and prompts support the runtime
- UI is only a surface
- dependency direction should stay inward
- tools are the boundary to external actions

## Personality and Tone

- GLaDOS is the overall theme and voice.
- Potato is the constrained local runtime running on limited resources.
- The local model is the execution brain, not the persona owner.
- The personality may be witty or GLaDOS-like, but it must never override safety, tool routing, or execution rules.

## Direction

- ReAct + direct tools is the center.
- `pipeline` stays only as a fallback until the direct path is stable.
- `AgentTask` stays only as thin workflow wrappers.
- Web UI is the ergonomic input surface for longer and multiline prompts, but it stays passive for orchestration and reporting.
- Planning stays a helper that produces compact step guidance.
- If a feature makes the model think twice about the same action, it is probably too much.

## 7B Model Notes

- Keep prompts small and phase-specific.
- Keep tool schemas strict and deterministic.
- Keep each step to one action and one observation.
- Add explicit fallback behavior when the model fails to choose a tool.
- Validate outputs aggressively instead of relying on model judgment.

## SOLID Map

### S - Single Responsibility

- `PipelineSession` should only coordinate user input, mode selection, and session state.
- `PlanningService` should only build planning artifacts and project context.
- `ReActSession` should only run the direct tool-driven execution loop.
- `PromptLibrary` should only store and render prompts.
- `ProjectMapBuilder` should only index, cache, and search project-map data.
- `AgentTask` implementations should each do one narrow workflow action.

### O - Open/Closed

- Add new capabilities by adding new tools or new narrow task types, not by adding more branches to the main loop.
- Keep prompt phases extensible by adding new prompt definitions instead of stuffing more logic into existing ones.

### L - Liskov Substitution

- Every task type should behave predictably under the same `IAgentTask` contract.
- A task should not require hidden state beyond the data the executor already passes in.

### I - Interface Segregation

- Split planning, execution, prompt loading, and UI reporting into separate services.
- Keep task interfaces minimal so tasks only depend on what they actually use.

### D - Dependency Inversion

- High-level orchestration should depend on abstractions for tools, prompt sources, and project search.
- The runtime should not depend on specific model behavior beyond the narrow direct-tool loop.

## Target Shape

### Core runtime

1. Input and slash commands stay in the CLI layer.
2. Execution uses a direct tool loop by default.
3. Planning only prepares compact step guidance when needed.
4. ProjectMap becomes a lazy search index, not a hot-path requirement.

### Prompt layer

- Keep one prompt per phase/job.
- Keep prompts short and versioned.
- Keep compiled defaults as the baseline and external prompt files as editable overrides.
- Remove duplicated rules from multiple prompts where possible.
- Do not let prompts become a second orchestration engine.

### Task layer

- Reduce `AgentTask` to a thin workflow adapter.
- Keep tasks deterministic and narrow.
- Prefer direct tool calls for read/search/edit/shell work.
- Use tasks only where they add real structure, not as an extra abstraction over the same action.

### UI layer

- Treat the Web UI connection as the ergonomic input surface for multiline prompts and richer text entry.
- Do not let it drive orchestration rules.
- Keep the CLI/runtime as the source of truth for execution state.

### UI safety

- Default UI mode should be observe-only.
- Any UI-driven input must require explicit opt-in or a session lock.
- Remote UI input must never preempt local CLI input without the user choosing that mode.
- Keep each Potato session isolated with its own session identity.
- The CLI remains the authority for execution and permission decisions.

## Refactor Phases

### Phase 1 - Stabilize the execution core

- Make direct tool calling the default path for task completion.
- Keep ReAct as the primary execution loop.
- Reduce task-level model prompting where the tool can do the work directly.

### Phase 2 - Simplify planning

- Keep `PlanningService` for compact step generation only.
- Stop sending full project-map context in planning prompts.
- Use `search-project-map` for discovery before exact file reads.

### Phase 3 - Narrow the task model

- Review each `AgentTask` and decide whether it is truly needed.
- Keep only tasks that express a stable workflow boundary.
- Remove tasks that just wrap model re-prompting or duplicate direct tool behavior.

### Phase 4 - Clean prompt responsibilities

- Split prompt text by phase and intent.
- Move repeated policy text into shared fragments only when needed.
- Preserve the ability for users to edit external prompt files generated from compiled defaults.
- Remove prompt rules that belong in runtime code.

### Phase 5 - Tighten project-map behavior

- Make cache validation cheap.
- Prefer timestamps and size checks over repeated full hashing.
- Refresh project-map data out of band when possible.

### Phase 6 - Clarify UI integration

- Keep Web UI input/reporting passive with respect to execution rules.
- Make multiline input and richer prompt entry work better than the raw CLI.
- Require explicit opt-in before any UI input can affect an active CLI session.
- Make sure UI messages do not affect tool routing or planning rules.

## Final Implementation Advice

- Keep `pipeline` only as a fallback, and remove it later if ReAct stays stable.
- Keep `AgentTask` thin; if it needs to invent content again, it is too heavy.
- Keep planning compact and helper-like, not a second agent personality.
- Keep ProjectMap lazy and cheap; search first, validate later.
- Keep the UI observe-only by default for safety.
- Keep prompts editable through external files even in a compiled build.
- Optimize for a 7B model by being deterministic, explicit, and small-step.

## What to Keep

- model selection
- slash commands
- file mention expansion
- session transcripts
- prompt file loading
- execution mode persistence
- startup greeting/banner
- project-map search
- permissioned shell execution

## What to Reconsider

- `pipeline` as a long-term default
- `AgentTask` as a deep abstraction layer
- heavy project-map validation on the hot path
- prompt duplication across phases
- Web UI as anything more than reporting

## Acceptance Criteria

- One successful action naturally leads to the next.
- The model no longer has to re-invent edits or commands.
- Planning context stays compact.
- Prompt files remain easy to read and reason about.
- Feature additions happen by adding new tools or narrow adapters, not by bloating the core loop.
