    function formatAssistantMessage(text) {
        let escaped = escapeHtml(text);

        if (escaped.includes('&lt;think&gt;')) {
            const parts = escaped.split('&lt;think&gt;');
            const beforeThink = parts[0];
            const rest = parts[1] || '';

            if (rest.includes('&lt;/think&gt;')) {
                const thinkParts = rest.split('&lt;/think&gt;');
                return `${formatCodeBlocks(beforeThink)}<details class="think-container"><summary>💭 Reasoning (Collapsed)</summary><div class="think-content">${formatCodeBlocks(thinkParts[0])}</div></details>${formatCodeBlocks(thinkParts[1])}`;
            } else {
                return `${formatCodeBlocks(beforeThink)}<details open class="think-container"><summary>🧠 Thinking...</summary><div class="think-content">${formatCodeBlocks(rest)}</div></details>`;
            }
        }
        return formatCodeBlocks(escaped);
    }

    function formatCodeBlocks(escapedText) {
        return escapedText.replace(/```([A-Za-z0-9_#+.-]*)?[^\S\r\n]*(?:\r?\n)([\s\S]*?)(?:\r?\n)?```/g, (_, language, code) => {
            const normalizedLanguage = normalizeCodeLanguage(language);
            const label = normalizedLanguage || 'code';

            return `<div class="code-block" data-language="${label}">`
                + '<div class="code-block-toolbar">'
                + `<span class="code-block-language">${label}</span>`
                + '<div class="code-block-actions">'
                + '<button type="button" class="code-block-button" data-code-action="copy" aria-label="Copy code block">Copy</button>'
                + '<button type="button" class="code-block-button" data-code-action="download" aria-label="Download code block">Download</button>'
                + '</div>'
                + '</div>'
                + `<pre><code>${trimCodeBlockEdges(code)}</code></pre>`
                + '</div>';
        });
    }

    function normalizeCodeLanguage(language) {
        return (language || '').trim().toLowerCase().replace(/[^a-z0-9_#+.-]/g, '') || '';
    }

    function trimCodeBlockEdges(code) {
        return code.replace(/^\r?\n/, '').replace(/\r?\n$/, '');
    }

    function initializeCodeBlockActions() {
        const chatBox = document.getElementById('chatBox');
        if (!chatBox || chatBox.dataset.codeBlockActionsInitialized === 'true') return;

        chatBox.dataset.codeBlockActionsInitialized = 'true';
        enhanceRenderedCodeBlocks(chatBox);

        const observer = new MutationObserver(() => {
            enhanceRenderedCodeBlocks(chatBox);
        });
        observer.observe(chatBox, {
            childList: true,
            subtree: true,
            characterData: true
        });

        chatBox.addEventListener('click', async (event) => {
            const button = event.target.closest('[data-code-action]');
            if (!button || !chatBox.contains(button)) return;

            const codeBlock = button.closest('.code-block');
            const codeElement = codeBlock?.querySelector('pre code');
            const code = codeElement?.textContent || '';
            if (!code) return;

            const action = button.dataset.codeAction;
            if (action === 'copy') {
                await copyCodeBlock(code, button);
            } else if (action === 'download') {
                downloadCodeBlock(code, codeBlock.dataset.language);
            }
        });
    }

    function enhanceRenderedCodeBlocks(root) {
        root.querySelectorAll('.message.assistant .message-content, .message.tool-call .message-content').forEach((contentElement) => {
            if (contentElement.querySelector('.code-block')) return;

            const text = contentElement.textContent || '';
            if (!hasCompleteCodeFence(text)) return;

            contentElement.innerHTML = formatAssistantMessage(text);
        });
    }

    function hasCompleteCodeFence(text) {
        const fenceMatches = text.match(/```/g);
        if (!fenceMatches || fenceMatches.length < 2) return false;

        return /```[A-Za-z0-9_#+.-]*[^\S\r\n]*(?:\r?\n)[\s\S]*?(?:\r?\n)?```/.test(text);
    }

    async function copyCodeBlock(code, button) {
        try {
            await writeClipboardText(code);
            setCodeButtonStatus(button, 'Copied');
        } catch {
            setCodeButtonStatus(button, 'Failed');
        }
    }

    function setCodeButtonStatus(button, text) {
        const originalText = button.dataset.defaultText || button.textContent;
        button.dataset.defaultText = originalText;
        button.textContent = text;
        window.setTimeout(() => {
            button.textContent = originalText;
        }, 1200);
    }

    function downloadCodeBlock(code, language) {
        const extension = getCodeFileExtension(language);
        const blob = new Blob([code], { type: 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `code-block.${extension}`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }

    function getCodeFileExtension(language) {
        const extensions = {
            csharp: 'cs',
            cs: 'cs',
            javascript: 'js',
            js: 'js',
            typescript: 'ts',
            ts: 'ts',
            html: 'html',
            css: 'css',
            json: 'json',
            bash: 'sh',
            shell: 'sh',
            sh: 'sh',
            powershell: 'ps1',
            python: 'py',
            py: 'py',
            markdown: 'md',
            md: 'md',
            xml: 'xml',
            yaml: 'yml',
            yml: 'yml',
            sql: 'sql'
        };

        return extensions[language] || 'txt';
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
