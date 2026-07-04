    function formatToolInvocation(toolName, args) {
        const entries = Object.entries(args || {});

        if (entries.length === 0) {
            return `${toolName}()`;
        }

        const formattedArgs = entries
            .map(([key, value]) => `${key}=${JSON.stringify(value)}`)
            .join(", ");

        return `${toolName}(${formattedArgs})`;
    }

    async function executeInternalTool(toolCall, args) {
        const statusText = document.getElementById('statusText');
        const normalizedToolCall = normalizeToolCall(toolCall, args);
        const toolName = normalizedToolCall.name;
        const toolCallId = toolCall.id || `call_${Date.now()}`;
        const toolArgs = normalizedToolCall.args;

        statusText.innerText = `Status: Executing tool (${toolName})`;

        const response = await fetch(`${baseEndpoint}/v1/tools/execute`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name: toolName,
                arguments: toolArgs
            })
        });

        if (!response.ok) {
            const message = `Tool ${toolName} failed with status ${response.status}.`;
            addToolMessage(message);
            return;
        }

        const result = await response.json();
        const output = result.output || "";
        const toolCallMessage = {
            _id: createMessageId(),
            role: "assistant",
            content: "",
            tool_calls: [
                {
                    id: toolCallId,
                    type: "function",
                    function: {
                        name: toolName,
                        arguments: JSON.stringify(toolArgs)
                    }
                }
            ]
        };
        const toolResultMessage = {
            _id: createMessageId(),
            role: "tool",
            name: toolName,
            tool_call_id: toolCallId,
            content: output
        };
        const outputEnvelope = parseArtifactEnvelope(output);
        if (outputEnvelope.artifacts.length > 0) {
            toolResultMessage.artifacts = outputEnvelope.artifacts;
        }

        chatHistory.push(toolCallMessage);
        chatHistory.push(toolResultMessage);
        persistActiveChat();
        updateContextUsage();

        if (pendingPermissionPromptElement) {
            pendingPermissionPromptElement.dataset.toolCallId = toolCallId;
            const permissionMessage = chatHistory.find((message) => message._id === pendingPermissionPromptMessageId);
            if (permissionMessage) {
                permissionMessage.tool_call_id = toolCallId;
                persistActiveChat();
            }

            pendingPermissionPromptElement = null;
            pendingPermissionPromptMessageId = null;
        } else {
            document.getElementById('chatBox').appendChild(createToolCallMessageElement(toolCallMessage));
        }

        const outputSummary = summarizeToolOutput(output, outputEnvelope.artifacts);
        addToolMessage(
            `🔧 ${toolName} -> ${outputSummary}`,
            toolResultMessage._id,
            { artifacts: outputEnvelope.artifacts });

        await executeAiRequest();
    }

    function normalizeToolCall(toolCall, args) {
        let toolName = toolCall.name;
        let toolArgs = args || {};

        if (typeof toolName === "string" && toolName.trim().startsWith("{")) {
            try {
                const parsed = JSON.parse(toolName);
                toolName = parsed.name || parsed.function?.name || toolName;
                toolArgs = parsed.arguments || parsed.function?.arguments || toolArgs;

                if (typeof toolArgs === "string") {
                    toolArgs = JSON.parse(toolArgs);
                }
            } catch {
                // Keep the original tool call; the server will return a clear not-found error.
            }
        }

        return {
            name: toolName,
            args: toolArgs || {}
        };
    }
