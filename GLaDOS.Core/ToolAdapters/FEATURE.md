# Tool Adapters

Tool adapters are a compatibility layer between parsed model tool calls and the tool router. They repair known, narrow tool-call argument shapes before execution or delegation.

They are not part of `GLaDOSAgent` orchestration. `GLaDOSAgent` only invokes `ToolCallAdapterPipeline`; adapter implementations own the details of specific argument repairs.

## Pipeline Rules

- Adapters run after `IAgentProtocol.ParseResponse(...)` and before `ToolRouter.RouteAsync(...)`.
- Adapters mutate the current `AgentToolCall` in place.
- Adapters should be small and conservative.
- Generic schema repair belongs in generic adapters or the `ToolCallAdapter` base class.
- Provider-specific behavior must check `AgentToolCall.Provider`.
- Tool-specific behavior should be named explicitly, for example `QwenEditToolCallAdapter`.
- Do not add protocol parsing here. Response parsing belongs in `Protocols`.
- Do not add execution logic here. Execution belongs in tools or the router.

## Base Class

`ToolCallAdapter` provides schema helpers shared by concrete adapters.

- `FindToolDefinition(...)` finds the selected tool definition by exact tool name.
- `RequiresProperties(...)` checks whether the selected tool schema declares required properties.
- `GetSingleRequiredProperty(...)` returns the one required parameter when the schema has exactly one.

Use these helpers when an adapter needs to reason about the selected tool schema. Do not duplicate schema lookup logic in individual adapters.

## SingleValueArgumentAdapter

Purpose: repair a common model fallback where arguments are emitted as a generic `value` property instead of the schema's real parameter name.

Input example:

```json
{
  "value": "/home/emiel/file.txt"
}
```

If the selected tool schema has exactly one required property, for example `file_path`, the adapter rewrites the call to:

```json
{
  "file_path": "/home/emiel/file.txt"
}
```

Additional behavior:

- Only runs when the arguments object has exactly one property named `value`.
- Only runs when `value` is a string.
- Does nothing if the tool schema does not have exactly one required property.
- Does nothing if that required property is already named `value`.
- If the target property name contains `path`, it tries to extract a clean absolute path from a longer string.

This adapter is generic. It is not Qwen-specific.

## QwenEditToolCallAdapter

Purpose: repair Qwen-originated edit calls that target replace-style edit tools requiring `old_string` and `new_string`.

This adapter is provider-scoped:

```csharp
toolCall.Provider == "Qwen"
```

It also requires the selected tool schema to declare both `old_string` and `new_string` as required properties.

Behavior:

- Maps `content` to `new_string` when Qwen emitted content under the wrong field.
- Normalizes quoted or double-encoded string values for `old_string` and `new_string`.
- Looks backward in chat history for the most recent matching read-file tool result.
- If no prior read result is found, rewrites the edit call into a read-file call for the same `file_path`.
- If `old_string` is missing, fills it with the latest read file content.
- If `old_string` does not match the latest read content and `new_string` looks like a full-file replacement, replaces `old_string` with the full latest read content.

Read-file extraction accepts common result shapes:

```json
{ "content": "..." }
{ "text": "..." }
{ "result": "..." }
{ "output": "..." }
{ "value": "..." }
```

It also strips fenced code blocks and line-number prefixes from read output.

This adapter is intentionally Qwen-specific because those edit argument mistakes came from Qwen-agent style calls. Do not generalize this adapter to other providers unless the provider is explicitly known to use the same edit dialect.

## ToolCallJson

`ToolCallJson` contains shared JSON argument normalization helpers.

Current behavior:

- `NormalizeStringArgument(...)` unwraps JSON string values that were encoded as quoted strings.
- It repeatedly unwraps values such as `"\"text\""` until a stable string is reached.
- If parsing fails but the value is wrapped in quotes, it strips one outer quote pair.

Keep this class limited to argument value normalization. Schema decisions belong in `ToolCallAdapter`; provider/tool-specific decisions belong in concrete adapters.

## Adding A New Adapter

When adding a new adapter:

1. Inherit from `ToolCallAdapter` unless there is a strong reason to implement `IToolCallAdapter` directly.
2. Make `CanAdapt(...)` narrow enough that unrelated tool calls are not changed.
3. Prefer exact provider checks for provider-specific repairs.
4. Prefer schema checks for generic tool-shape repairs.
5. Register the adapter in `AddCoreServicesBootstrapper`.
6. Add or update this file with the adapter's purpose, input shape, output shape, and safety boundaries.
