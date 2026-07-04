    function createPermissionPromptElement(message, resolve = null) {
        const permDiv = document.createElement('div');
        permDiv.className = 'message tool-call permission-prompt';
        permDiv.dataset.historyId = message._id;
        permDiv.innerHTML = `
            <p class="permission-text permission-title">🛡️ Permission required</p>
            <p class="permission-text">GLaDOS wants to run <strong>${escapeHtml(message.invocation || message.content)}</strong>. Do you approve?</p>
            <div class="permission-actions">
                <button class="permission-button yes" data-choice="yes">Yes</button>
                <button class="permission-button no" data-choice="no">No</button>
                <button class="permission-button always" data-choice="always">Always during this chat</button>
            </div>
        `;

        permDiv.querySelectorAll('.permission-button').forEach((button) => {
            button.addEventListener('click', () => handlePermissionChoice(message, button.dataset.choice, permDiv, resolve));
        });

        return permDiv;
    }

    async function handlePermissionChoice(message, choice, permDiv, resolve = null) {
        const approved = choice === 'yes' || choice === 'always';
        const pendingToolCall = getPendingToolCall(message);
        const toolName = pendingToolCall?.name || "";

        if (choice === 'always' && toolName) {
            alwaysAllowTools.add(toolName);
        }

        const permissionContent = approved
            ? `🔧 [TOOL CALL]: ${message.invocation} -> Status: Approved`
            : `❌ [TOOL CALL]: ${message.invocation} -> Status: Denied by user`;

        Object.assign(message, {
            content: permissionContent,
            permission_status: approved ? "approved" : "denied"
        });
        persistActiveChat();
        updateContextUsage();

        permDiv.innerHTML = approved
            ? `🔧 [TOOL CALL]: ${escapeHtml(message.invocation)} -> Status: Approved`
            : `❌ [TOOL CALL]: ${escapeHtml(message.invocation)} -> Status: Denied by user`;
        addMessageActions(permDiv);

        pendingPermissionPromptElement = approved ? permDiv : null;
        pendingPermissionPromptMessageId = approved ? message._id : null;
        document.getElementById('statusText').innerText = approved && toolName
            ? `Status: Tool approved (${toolName})`
            : `Status: Tool denied${toolName ? ` (${toolName})` : ""}`;
        document.getElementById('submitBtn').disabled = false;

        if (resolve) {
            resolve(approved);
            return;
        }

        if (!approved) return;

        try {
            await GLaDOSTools.executeApprovedToolCall(pendingToolCall, message.pending_tool_args || {}, {
                addToolMessage,
                requestToolPermission,
                executeInternalTool,
                executeAiRequest,
                escapeHtml
            });
        } catch (error) {
            document.getElementById('statusText').innerText = `Status: Failed to resume tool (${error.message})`;
            addToolMessage(`Tool ${toolName || "unknown"} failed after approval: ${error.message}`);
        }
    }

    function getPendingToolCall(message) {
        if (message.pending_tool_call?.name) {
            return message.pending_tool_call;
        }

        const invocationMatch = /^([A-Za-z_][\w.-]*)\(/.exec(message.invocation || "");
        if (invocationMatch) {
            return { name: invocationMatch[1] };
        }

        return { name: "" };
    }

    function requestToolPermission(tool, args, toolCall = null) {
        const chatBox = document.getElementById('chatBox');
        const statusText = document.getElementById('statusText');
        const submitBtn = document.getElementById('submitBtn');

        if (alwaysAllowTools.has(tool.name)) {
            return Promise.resolve(true);
        }

        statusText.innerText = `Status: Waiting for permission to use ${tool.name}...`;
        submitBtn.disabled = true;

        const invocation = formatToolInvocation(tool.name, args);
        const permissionMessage = createUiHistoryMessage(`Permission required: ${invocation}`, {
            permissionStatus: "pending",
            invocation,
            pendingToolCall: toolCall || { name: tool.name },
            pendingToolArgs: args || {}
        });
        chatHistory.push(permissionMessage);
        persistActiveChat();
        updateContextUsage();

        return new Promise((resolve) => {
            const permDiv = createPermissionPromptElement(permissionMessage, resolve);
            chatBox.appendChild(permDiv);
            chatBox.scrollTop = chatBox.scrollHeight;
        });
    }
