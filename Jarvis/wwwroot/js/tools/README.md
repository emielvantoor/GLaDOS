# Jarvis browser tools

Each tool lives in its own JavaScript file and registers itself with `JarvisTools.register(...)`.

To add a tool:

1. Create a new file in this folder, for example `get-system-time.js`.
2. Copy the pattern below and adjust `name`, `icon`, `schema`, and `handle`.
3. Add the file name to `toolScripts` in `index.js`.

```js
JarvisTools.register({
    name: "my_tool",
    icon: "🧰",
    schema: {
        name: "my_tool",
        description: "Describe when the model should use this tool.",
        parameters: {
            type: "object",
            properties: {
                value: {
                    type: "string",
                    description: "Describe this argument."
                }
            },
            required: ["value"]
        }
    },
    handle(args, context) {
        context.addToolMessage(`Tool called with: ${args.value}`);
    }
});
```

Available handler context:

- `context.addToolMessage(message)` shows a tool message in the chat.
- `context.askSearchPermission(query)` shows the web-search permission prompt.
- `context.executeAiRequest(searchExecuted, query)` continues the AI request flow.
- `context.escapeHtml(text)` escapes HTML for safe display.
