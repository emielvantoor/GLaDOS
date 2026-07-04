    function formatAssistantMessage(text) {
        let escaped = escapeHtml(text);

        if (escaped.includes('&lt;think&gt;')) {
            const parts = escaped.split('&lt;think&gt;');
            const beforeThink = parts[0];
            const rest = parts[1] || '';

            if (rest.includes('&lt;/think&gt;')) {
                const thinkParts = rest.split('&lt;/think&gt;');
                return `${beforeThink}<details class="think-container"><summary>💭 Reasoning (Collapsed)</summary><div class="think-content">${thinkParts[0]}</div></details>${thinkParts[1]}`;
            } else {
                return `${beforeThink}<details open class="think-container"><summary>🧠 Thinking...</summary><div class="think-content">${rest}</div></details>`;
            }
        }
        return escaped;
    }

    function createNewAssistantBubble() {
        const chatBox = document.getElementById('chatBox');
        const bubbleId = 'ai-' + Date.now();

        const div = document.createElement('div');
        div.className = 'message assistant';
        div.id = bubbleId;

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';
        div.appendChild(contentDiv);

        chatBox.appendChild(div);

        currentAiBubbleElement = document.getElementById(bubbleId);
        currentBubbleContentBuffer = "";
    }

    function showAssistantLoading() {
        createNewAssistantBubble();
        currentAiBubbleElement.classList.add('loading');
        currentAiBubbleElement.querySelector('.message-content').innerHTML = `
            <span class="loading-spinner" aria-hidden="true"></span>
            <span class="loading-text">GLaDOS is thinking</span>
        `;
        currentAiBubbleElement.setAttribute('aria-live', 'polite');
        document.getElementById('chatBox').scrollTop = document.getElementById('chatBox').scrollHeight;
    }

    function clearAssistantLoading() {
        if (!currentAiBubbleElement) return;
        currentAiBubbleElement.classList.remove('loading');
        currentAiBubbleElement.removeAttribute('aria-live');
    }

    function finalizeCurrentAssistantBubble(historyId) {
        if (!currentAiBubbleElement) return;
        clearAssistantLoading();
        currentAiBubbleElement.dataset.historyId = historyId;
        addMessageActions(currentAiBubbleElement);
    }

    function appendAssistantContent(content) {
        const chatBox = document.getElementById('chatBox');

        if (content.startsWith('\n[System:') || content.startsWith('\n[Systeem:')) {
            const systemDiv = createMessageElement('tool-call', content.trim());
            chatBox.appendChild(systemDiv);

            createNewAssistantBubble();
            chatBox.scrollTop = chatBox.scrollHeight;
            return;
        }

        if (!currentAiBubbleElement) {
            createNewAssistantBubble();
        }

        clearAssistantLoading();
        currentBubbleContentBuffer += content;
        currentAiBubbleElement.querySelector('.message-content').innerHTML = formatAssistantMessage(currentBubbleContentBuffer);
        chatBox.scrollTop = chatBox.scrollHeight;
    }

    function addToolMessage(message, historyId = null, options = {}) {
        const chatBox = document.getElementById('chatBox');
        let resolvedHistoryId = historyId;
        const envelope = parseArtifactEnvelope(message);
        const artifacts = Array.isArray(options.artifacts) ? options.artifacts : envelope.artifacts;
        const displayMessage = options.displayText || (artifacts.length > 0 ? summarizeToolOutput(envelope.text || message, artifacts) : message);

        if (!resolvedHistoryId) {
            const historyMessage = createUiHistoryMessage(displayMessage, { artifacts });
            chatHistory.push(historyMessage);
            persistActiveChat();
            updateContextUsage();
            resolvedHistoryId = historyMessage._id;
        }

        const toolDiv = createMessageElement('tool-call', displayMessage, { historyId: resolvedHistoryId, artifacts });
        chatBox.appendChild(toolDiv);
        chatBox.scrollTop = chatBox.scrollHeight;

        if (!historyId && pendingPermissionPromptElement) {
            pendingPermissionPromptElement = null;
            pendingPermissionPromptMessageId = null;
        }
    }
