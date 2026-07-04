(function () {
    const tools = [];

    function register(tool) {
        if (!tool || !tool.name || !tool.schema) {
            throw new Error("JarvisTools.register requires a tool with name and schema.");
        }

        if (tools.some(existing => existing.name === tool.name)) {
            throw new Error(`Tool already registered: ${tool.name}`);
        }

        tools.push({
            permitted: "User",
            ...tool,
            permitted: normalizePermission(tool.permitted)
        });
    }

    function getOpenAiTools() {
        return tools.map(tool => ({
            type: "function",
            permitted: tool.permitted,
            function: tool.schema
        }));
    }

    function renderToolList(container) {
        container.innerHTML = "";

        tools.forEach(tool => {
            const tag = document.createElement("div");
            tag.className = "tool-tag";
            tag.textContent = `${tool.icon || "🧰"} ${tool.name} (${tool.permitted})`;
            container.appendChild(tag);
        });
    }

    async function handleToolCall(toolCall, args, context) {
        const tool = tools.find(candidate => candidate.name === toolCall.name);
        const toolMetadata = tool || {
            name: toolCall.name,
            permitted: "User",
            schema: { name: toolCall.name }
        };

        if (toolMetadata.permitted === "User") {
            const approved = await context.requestToolPermission(toolMetadata, args, toolCall);
            if (!approved) {
                context.addToolMessage(`Tool ${toolMetadata.name} was denied by user.`);
                return;
            }
        }

        await executeApprovedToolCall(toolCall, args, context);
    }

    async function executeApprovedToolCall(toolCall, args, context) {
        const tool = tools.find(candidate => candidate.name === toolCall.name);

        if (!tool || !tool.handle) {
            await context.executeInternalTool(toolCall, args);
            return;
        }

        await tool.handle(args, context);
    }

    function normalizePermission(value) {
        if (value === "Automatic" || value === "automatic") {
            return "Automatic";
        }

        return "User";
    }

    window.JarvisTools = {
        register,
        getOpenAiTools,
        handleToolCall,
        executeApprovedToolCall,
        renderToolList
    };
})();
