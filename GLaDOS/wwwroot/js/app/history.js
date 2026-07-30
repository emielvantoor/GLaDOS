    function createMessageId() {
        return `msg-${nextMessageId++}`;
    }

    function createChatHistoryMessage(role, content) {
        return {
            _id: createMessageId(),
            role,
            content
        };
    }

    function getRequestMessages() {
        return chatHistory
            .filter((message) => !message.ui_only)
            .map(({ _id, ui_only, artifacts, ...message }) => message);
    }

    function removeFromChatHistory(messageId) {
        if (!messageId) return;
        if (getActiveChat()?.usesSession !== false) return;
        const message = chatHistory.find((candidate) => candidate._id === messageId);
        const linkedToolCallIds = new Set();

        if (message?.role === "assistant" && Array.isArray(message.tool_calls)) {
            message.tool_calls.forEach((toolCall) => {
                if (toolCall?.id) linkedToolCallIds.add(toolCall.id);
            });
        }

        if (message?.role === "tool" && message.tool_call_id) {
            linkedToolCallIds.add(message.tool_call_id);
        }

        if (message?.ui_only && message.tool_call_id) {
            linkedToolCallIds.add(message.tool_call_id);
        }

        chatHistory = chatHistory.filter((candidate) => {
            if (candidate._id === messageId) return false;
            if (candidate.ui_only && linkedToolCallIds.has(candidate.tool_call_id)) return false;
            if (candidate.role === "tool" && linkedToolCallIds.has(candidate.tool_call_id)) return false;
            if (candidate.role === "assistant" && Array.isArray(candidate.tool_calls)) {
                return !candidate.tool_calls.some((toolCall) => linkedToolCallIds.has(toolCall?.id));
            }

            return true;
        });
        persistActiveChat();
        updateContextUsage();
    }
