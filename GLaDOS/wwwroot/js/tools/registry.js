(function () {
    const tools = [];

    function register(tool) {
        if (!tool || !tool.name || !tool.schema) {
            throw new Error("GLaDOSTools.register requires a tool with name and schema.");
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

    async function renderToolList(container, options = {}) {
        container.innerHTML = "";

        renderToolSection(container, "Browser tools", tools.map(tool => ({
            name: tool.name,
            description: tool.schema.description || "",
            permitted: tool.permitted,
            source: "Browser",
            icon: tool.icon || "🧰"
        })));

        if (!options.endpoint) return;

        try {
            const response = await fetch(`${options.endpoint}/v1/tools`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const payload = await response.json();
            const internalTools = normalizeInternalTools(payload.data || payload || []);
            renderToolSection(container, "Internal tools", internalTools);
        } catch (error) {
            const message = document.createElement("div");
            message.className = "tools-empty";
            message.textContent = `Internal tools unavailable (${error.message}).`;
            container.appendChild(message);
        }
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

    function normalizeInternalTools(items) {
        return items.map(item => {
            const schema = item.function || item.schema || item;
            return {
                name: schema.name || item.name || "unknown_tool",
                description: schema.description || item.description || "",
                permitted: item.permitted || "User",
                source: item.source || "Internal",
                icon: item.permitted === "Automatic" ? "⚙️" : "🛠️"
            };
        });
    }

    function renderToolSection(container, title, sectionTools) {
        const section = document.createElement("div");
        section.className = "tool-section";

        const heading = document.createElement("div");
        heading.className = "tool-section-title";
        heading.textContent = title;
        section.appendChild(heading);

        if (sectionTools.length === 0) {
            const empty = document.createElement("div");
            empty.className = "tools-empty";
            empty.textContent = "No tools registered.";
            section.appendChild(empty);
            container.appendChild(section);
            return;
        }

        sectionTools.forEach(tool => {
            const item = document.createElement("div");
            item.className = "tool-item";

            const tag = document.createElement("div");
            tag.className = "tool-tag";
            tag.textContent = `${tool.icon || "🧰"} ${tool.name} (${tool.permitted})`;
            if (tool.description) {
                tag.dataset.tooltip = tool.description;
                tag.tabIndex = 0;
            }
            item.appendChild(tag);

            section.appendChild(item);
        });

        container.appendChild(section);
    }

    window.GLaDOSTools = {
        register,
        getOpenAiTools,
        handleToolCall,
        executeApprovedToolCall,
        renderToolList
    };
})();
