    function getMessageText(messageElement) {
        const contentElement = messageElement.querySelector('.message-content');
        return contentElement ? contentElement.innerText : messageElement.innerText;
    }

    async function copyMessageText(messageElement, copyButton) {
        const text = getMessageText(messageElement);

        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
            } else {
                const textarea = document.createElement('textarea');
                textarea.value = text;
                textarea.style.position = 'fixed';
                textarea.style.opacity = '0';
                document.body.appendChild(textarea);
                textarea.select();
                document.execCommand('copy');
                textarea.remove();
            }

            copyButton.textContent = 'Copied';
            window.setTimeout(() => {
                copyButton.textContent = 'Copy';
            }, 1200);
        } catch {
            copyButton.textContent = 'Failed';
            window.setTimeout(() => {
                copyButton.textContent = 'Copy';
            }, 1200);
        }
    }

    function addMessageActions(messageElement) {
        if (!messageElement || messageElement.querySelector('.message-actions')) return;

        if (!messageElement.querySelector('.message-content')) {
            const contentDiv = document.createElement('div');
            contentDiv.className = 'message-content';
            while (messageElement.firstChild) {
                contentDiv.appendChild(messageElement.firstChild);
            }
            messageElement.appendChild(contentDiv);
        }

        const actions = document.createElement('div');
        actions.className = 'message-actions';

        const copyButton = document.createElement('button');
        copyButton.type = 'button';
        copyButton.className = 'message-action';
        copyButton.textContent = 'Copy';
        copyButton.setAttribute('aria-label', 'Copy message');
        copyButton.addEventListener('click', () => copyMessageText(messageElement, copyButton));

        const deleteButton = document.createElement('button');
        deleteButton.type = 'button';
        deleteButton.className = 'message-action danger';
        deleteButton.textContent = 'Delete';
        deleteButton.setAttribute('aria-label', 'Delete message');
        deleteButton.addEventListener('click', () => {
            removeFromChatHistory(messageElement.dataset.historyId);
            renderActiveChatMessages();
        });

        actions.append(copyButton, deleteButton);
        messageElement.appendChild(actions);
    }

    function createMessageElement(role, content, options = {}) {
        const div = document.createElement('div');
        div.className = `message ${role}`;

        if (options.historyId) {
            div.dataset.historyId = options.historyId;
        }

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';

        if (options.html) {
            contentDiv.innerHTML = content;
        } else {
            contentDiv.textContent = content;
        }

        div.appendChild(contentDiv);
        if (Array.isArray(options.artifacts) && options.artifacts.length > 0) {
            div.appendChild(createArtifactListElement(options.artifacts));
        }
        if (options.actions !== false) {
            addMessageActions(div);
        }
        return div;
    }

    function createUiHistoryMessage(content, options = {}) {
        return {
            _id: createMessageId(),
            role: "tool-call",
            content,
            ui_only: true,
            tool_call_id: options.toolCallId || "",
            permission_status: options.permissionStatus || "",
            invocation: options.invocation || "",
            pending_tool_call: options.pendingToolCall || null,
            pending_tool_args: options.pendingToolArgs || null,
            artifacts: Array.isArray(options.artifacts) ? options.artifacts : undefined
        };
    }

    function createStoredMessageElement(message) {
        if (message.ui_only && message.permission_status === "pending") {
            return createPermissionPromptElement(message);
        }

        if (message.ui_only || message.role === "tool-call") {
            const element = createMessageElement("tool-call", message.content, { historyId: message._id });
            element.dataset.toolCallId = message.tool_call_id || "";
            return element;
        }

        if (message.role === "assistant" && Array.isArray(message.tool_calls) && message.tool_calls.length > 0) {
            return createToolCallMessageElement(message);
        }

        if (message.role === "tool") {
            return createToolResultMessageElement(message);
        }

        const envelope = parseArtifactEnvelope(message.content);
        const artifacts = Array.isArray(message.artifacts) && message.artifacts.length > 0
            ? message.artifacts
            : envelope.artifacts;
        const textContent = envelope.text || getMessageContentText(message.content);
        const html = message.role === "assistant" ? formatAssistantMessage(textContent) : null;
        return createMessageElement(
            message.role,
            html || textContent,
            { historyId: message._id, html: Boolean(html), artifacts });
    }

    function createToolCallMessageElement(message) {
        const toolCall = message.tool_calls[0];
        const toolName = toolCall?.function?.name || message.name || "tool";
        const args = parseToolCallArguments(toolCall?.function?.arguments);
        const element = createMessageElement(
            "tool-call",
            `🔧 [TOOL CALL]: ${formatToolInvocation(toolName, args)} -> Status: Approved`,
            { historyId: message._id });
        element.dataset.toolCallId = toolCall?.id || "";
        return element;
    }

    function createToolResultMessageElement(message) {
        const envelope = parseArtifactEnvelope(message.content);
        const artifacts = Array.isArray(message.artifacts) && message.artifacts.length > 0
            ? message.artifacts
            : envelope.artifacts;
        const summary = envelope.text || summarizeToolOutput(message.content, artifacts);
        const element = createMessageElement(
            "tool-call",
            `🔧 ${message.name || "tool"} -> ${summary}`,
            { historyId: message._id, artifacts });
        element.dataset.toolCallId = message.tool_call_id || "";
        return element;
    }

    function summarizeToolOutput(content, artifacts = []) {
        const text = getMessageContentText(content).trim();
        if (artifacts.length === 0) return text;
        const label = artifacts.length === 1 ? artifacts[0].filename : `${artifacts.length} files`;
        return text && text.length < 500 ? text : `Created ${label}`;
    }

    function parseToolCallArguments(rawArguments) {
        if (!rawArguments) return {};
        if (typeof rawArguments === "object") return rawArguments;

        try {
            return JSON.parse(rawArguments);
        } catch {
            return { query: rawArguments };
        }
    }

    function isToolCallCoveredByUiMessage(message) {
        if (message.role !== "assistant" || !Array.isArray(message.tool_calls)) return false;

        return message.tool_calls.some((toolCall) =>
            toolCall?.id &&
            chatHistory.some((candidate) => candidate.ui_only && candidate.tool_call_id === toolCall.id));
    }
