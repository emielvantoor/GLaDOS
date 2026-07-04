(function () {
    const tools = [];

    function register(tool) {
        if (!tool || !tool.name || !tool.schema) {
            throw new Error("JarvisTools.register requires a tool with name and schema.");
        }

        if (tools.some(existing => existing.name === tool.name)) {
            throw new Error(`Tool already registered: ${tool.name}`);
        }

        tools.push(tool);
    }

    function getOpenAiTools() {
        return tools.map(tool => ({
            type: "function",
            function: tool.schema
        }));
    }

    function renderToolList(container) {
        container.innerHTML = "";

        tools.forEach(tool => {
            const tag = document.createElement("div");
            tag.className = "tool-tag";
            tag.textContent = `${tool.icon || "🧰"} ${tool.name}`;
            container.appendChild(tag);
        });
    }

    async function handleToolCall(toolCall, args, context) {
        const tool = tools.find(candidate => candidate.name === toolCall.name);

        if (!tool || !tool.handle) {
            context.addToolMessage(`⚠️ Geen client handler gevonden voor tool: ${toolCall.name}`);
            return;
        }

        await tool.handle(args, context);
    }

    window.JarvisTools = {
        register,
        getOpenAiTools,
        handleToolCall,
        renderToolList
    };
})();
