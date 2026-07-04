    let chatHistory = [];
    let nextMessageId = 1;
    let baseEndpoint = "";
    let alwaysAllowSearch = false;
    const defaultContextSize = 32768;
    const chatsStorageKey = "jarvis-chats";
    const activeChatStorageKey = "jarvis-active-chat-id";
    const welcomeMessage = "Hello Emiel! I'm ready to assist you through your local hardware. Feel free to ask me anything!";
    let chats = [];
    let activeChatId = null;

    let currentAiBubbleElement = null;
    let currentAiHistoryId = null;
    let currentBubbleContentBuffer = "";
    const themeStorageKey = "jarvis-theme-mode";
    const themeModes = ["auto", "light", "dark"];

    window.addEventListener('DOMContentLoaded', async () => {
        if (window.JarvisToolsReady) {
            await window.JarvisToolsReady;
        }

        const currentHost = window.location.host;
        const protocol = window.location.protocol;
        baseEndpoint = currentHost ? `${protocol}//${currentHost}` : "http://localhost:11434";
        document.getElementById('endpoint').value = `${baseEndpoint}/v1/chat/completions`;
        document.getElementById('themeBtn').addEventListener('click', toggleTheme);
        document.getElementById('newChatBtn').addEventListener('click', createAndSwitchToNewChat);
        document.getElementById('deleteChatBtn').addEventListener('click', deleteActiveChat);
        document.getElementById('prompt').addEventListener('keydown', handleKeyPress);
        document.getElementById('submitBtn').addEventListener('click', sendPrompt);
        document.getElementById('temperature').addEventListener('input', (event) => {
            updateTempVal(event.target.value);
        });
        document.getElementById('contextSize').addEventListener('input', updateContextUsage);
        document.getElementById('inspectorToggle').addEventListener('click', toggleInspector);
        initializeChats();
        JarvisTools.renderToolList(document.getElementById('tools-list'));
        loadModels();
        updateContextUsage();
        refreshRuntimeMemoryUsage();

        applyTheme();
        window.setInterval(applyTheme, 60 * 1000);
        window.setInterval(refreshRuntimeMemoryUsage, 5 * 1000);
    });

    function toggleTheme() {
        const currentMode = getThemeMode();
        const nextMode = themeModes[(themeModes.indexOf(currentMode) + 1) % themeModes.length];
        saveThemeMode(nextMode);
        applyTheme();
    }

    function getThemeMode() {
        let savedMode = null;

        try {
            savedMode = localStorage.getItem(themeStorageKey);
        } catch {
            savedMode = null;
        }

        return themeModes.includes(savedMode) ? savedMode : "auto";
    }

    function saveThemeMode(mode) {
        try {
            localStorage.setItem(themeStorageKey, mode);
        } catch {
            // Storage can be unavailable when the page is opened directly or privacy settings block it.
        }
    }

    function initializeChats() {
        chats = loadChats();
        activeChatId = loadActiveChatId();

        if (!chats.some((chat) => chat.id === activeChatId)) {
            activeChatId = chats[0]?.id || null;
        }

        if (!activeChatId) {
            const chat = createChat();
            chats = [chat];
            activeChatId = chat.id;
        }

        nextMessageId = getNextStoredMessageNumber();
        syncActiveChatHistory();
        renderChatList();
        renderActiveChatMessages();
        saveChats();
    }

    function loadChats() {
        try {
            const raw = localStorage.getItem(chatsStorageKey);
            const parsed = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(parsed)) return [];

            return parsed
                .filter((chat) => chat && typeof chat.id === "string" && Array.isArray(chat.messages))
                .map((chat) => ({
                    id: chat.id,
                    title: typeof chat.title === "string" && chat.title.trim() ? chat.title : "New Chat",
                    createdAt: chat.createdAt || new Date().toISOString(),
                    updatedAt: chat.updatedAt || chat.createdAt || new Date().toISOString(),
                    messages: chat.messages.filter((message) =>
                        message &&
                        typeof message._id === "string" &&
                        typeof message.role === "string" &&
                        typeof message.content === "string")
                }));
        } catch {
            return [];
        }
    }

    function loadActiveChatId() {
        try {
            return localStorage.getItem(activeChatStorageKey);
        } catch {
            return null;
        }
    }

    function saveChats() {
        try {
            localStorage.setItem(chatsStorageKey, JSON.stringify(chats));
            localStorage.setItem(activeChatStorageKey, activeChatId || "");
        } catch {
            document.getElementById('statusText').innerText = 'Status: Browser storage is unavailable; chats will not persist.';
        }
    }

    function createChat(title = "New Chat") {
        const now = new Date().toISOString();
        return {
            id: `chat-${Date.now()}-${Math.random().toString(16).slice(2)}`,
            title,
            createdAt: now,
            updatedAt: now,
            messages: []
        };
    }

    function getActiveChat() {
        return chats.find((chat) => chat.id === activeChatId) || null;
    }

    function syncActiveChatHistory() {
        const chat = getActiveChat();
        chatHistory = chat ? chat.messages : [];
        alwaysAllowSearch = false;
    }

    function persistActiveChat() {
        const chat = getActiveChat();
        if (!chat) return;

        chat.messages = chatHistory;
        chat.updatedAt = new Date().toISOString();
        updateChatTitle(chat);
        saveChats();
        renderChatList();
    }

    function updateChatTitle(chat) {
        const firstUserMessage = chat.messages.find((message) => message.role === "user");
        if (!firstUserMessage) {
            chat.title = "New Chat";
            return;
        }

        const compactTitle = firstUserMessage.content.replace(/\s+/g, " ").trim();
        chat.title = compactTitle.length > 34 ? `${compactTitle.slice(0, 34)}...` : compactTitle;
    }

    function createAndSwitchToNewChat() {
        if (document.getElementById('submitBtn').disabled) return;

        const chat = createChat();
        chats.unshift(chat);
        activeChatId = chat.id;
        syncActiveChatHistory();
        renderChatList();
        renderActiveChatMessages();
        saveChats();
        updateContextUsage();
        document.getElementById('statusText').innerText = 'Status: New chat created.';
    }

    function switchChat(chatId) {
        if (document.getElementById('submitBtn').disabled || chatId === activeChatId) return;
        if (!chats.some((chat) => chat.id === chatId)) return;

        activeChatId = chatId;
        syncActiveChatHistory();
        renderChatList();
        renderActiveChatMessages();
        saveChats();
        updateContextUsage();
        document.getElementById('statusText').innerText = 'Status: Chat loaded.';
    }

    function deleteActiveChat() {
        if (document.getElementById('submitBtn').disabled || !activeChatId) return;

        chats = chats.filter((chat) => chat.id !== activeChatId);
        if (chats.length === 0) {
            chats.push(createChat());
        }

        activeChatId = chats[0].id;
        syncActiveChatHistory();
        renderChatList();
        renderActiveChatMessages();
        saveChats();
        updateContextUsage();
        document.getElementById('statusText').innerText = 'Status: Chat deleted.';
    }

    function renderChatList() {
        const chatList = document.getElementById('chatList');
        chatList.innerHTML = '';

        chats
            .slice()
            .sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt))
            .forEach((chat) => {
                const item = document.createElement('button');
                item.type = 'button';
                item.className = `chat-list-item${chat.id === activeChatId ? ' active' : ''}`;
                item.dataset.chatId = chat.id;
                item.title = chat.title;
                item.addEventListener('click', () => switchChat(chat.id));

                const title = document.createElement('div');
                title.className = 'chat-list-title';
                title.textContent = chat.title;

                const meta = document.createElement('div');
                meta.className = 'chat-list-meta';
                meta.textContent = `${chat.messages.length} messages`;

                item.append(title, meta);
                chatList.appendChild(item);
            });
    }

    function renderActiveChatMessages() {
        const chatBox = document.getElementById('chatBox');
        chatBox.innerHTML = '';

        if (chatHistory.length === 0) {
            chatBox.appendChild(createMessageElement('assistant', welcomeMessage, { actions: false }));
            return;
        }

        chatHistory.forEach((message) => {
            const html = message.role === "assistant" ? formatAssistantMessage(message.content) : null;
            chatBox.appendChild(createMessageElement(
                message.role,
                html || message.content,
                { historyId: message._id, html: Boolean(html) }));
        });

        chatBox.scrollTop = chatBox.scrollHeight;
    }

    function getNextStoredMessageNumber() {
        let maxId = 0;
        chats.forEach((chat) => {
            chat.messages.forEach((message) => {
                const match = /^msg-(\d+)$/.exec(message._id);
                if (match) {
                    maxId = Math.max(maxId, parseInt(match[1], 10));
                }
            });
        });

        return maxId + 1;
    }

    function getAutomaticTheme(now = new Date()) {
        const hour = now.getHours();
        return hour >= 7 && hour < 19 ? "light" : "dark";
    }

    function getTimeTone(now = new Date()) {
        const hour = now.getHours();

        if (hour >= 5 && hour < 10) return "morning";
        if (hour >= 10 && hour < 17) return "day";
        if (hour >= 17 && hour < 21) return "evening";
        return "night";
    }

    function applyTheme() {
        const mode = getThemeMode();
        const activeTheme = mode === "auto" ? getAutomaticTheme() : mode;
        const body = document.body;
        const themeBtn = document.getElementById('themeBtn');

        body.dataset.timeTone = getTimeTone();

        if (activeTheme === "dark") {
            body.setAttribute('data-theme', 'dark');
        } else {
            body.removeAttribute('data-theme');
        }

        if (mode === "auto") {
            themeBtn.innerText = activeTheme === "dark" ? "🌓 Auto: Night" : "🌓 Auto: Day";
        } else if (mode === "dark") {
            themeBtn.innerText = "🌙 Dark Mode";
        } else {
            themeBtn.innerText = "☀️ Light Mode";
        }

        themeBtn.title = "Theme mode: Auto, Light, Dark";
    }

    function escapeHtml(text) {
        if (!text) return "";
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

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
        return chatHistory.map(({ _id, ...message }) => message);
    }

    function removeFromChatHistory(messageId) {
        if (!messageId) return;
        chatHistory = chatHistory.filter((message) => message._id !== messageId);
        persistActiveChat();
        updateContextUsage();
    }

    function getContextSize() {
        const value = parseInt(document.getElementById('contextSize').value, 10);
        return Number.isFinite(value) && value > 0 ? value : defaultContextSize;
    }

    function estimateTokenCount(text) {
        if (!text) return 0;
        return Math.ceil(text.length / 4);
    }

    function getChatContextUsage() {
        return chatHistory.reduce((total, message) => {
            return total + estimateTokenCount(`${message.role}\n${message.content || ""}`);
        }, 0);
    }

    function formatNumber(value) {
        return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
    }

    function formatMegabytes(value) {
        return value.toLocaleString(undefined, {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        });
    }

    function updateContextUsage() {
        const contextSize = getContextSize();
        const used = getChatContextUsage();
        const percent = contextSize > 0 ? Math.min((used / contextSize) * 100, 100) : 0;
        const usageElement = document.getElementById('contextUsage');
        const meterFill = document.getElementById('contextMeterFill');

        usageElement.innerText = `${formatNumber(used)} / ${formatNumber(contextSize)} tokens used`;
        meterFill.style.width = `${percent}%`;
        meterFill.classList.toggle('warning', percent >= 75 && percent < 90);
        meterFill.classList.toggle('danger', percent >= 90);
    }

    async function refreshRuntimeMemoryUsage() {
        const memoryElement = document.getElementById('contextMemoryUsage');
        if (!memoryElement || !baseEndpoint) return;

        try {
            const response = await fetch(`${baseEndpoint}/v1/runtime/memory`, { method: 'GET' });
            if (!response.ok) throw new Error();

            const data = await response.json();
            const ramText = data.system_ram_total_mb
                ? `RAM: ${formatMegabytes(data.system_ram_used_mb)} / ${formatMegabytes(data.system_ram_total_mb)} MB`
                : `Process RAM: ${formatMegabytes(data.process_ram_mb)} MB`;
            const processText = `Jarvis: ${formatMegabytes(data.process_ram_mb)} MB`;
            const heapText = `Heap: ${formatMegabytes(data.managed_heap_mb)} MB`;
            const gpuText = data.gpu_vram_total_mb
                ? `VRAM: ${formatMegabytes(data.gpu_vram_used_mb)} / ${formatMegabytes(data.gpu_vram_total_mb)} MB${data.gpu_name ? ` (${data.gpu_name})` : ""}`
                : "VRAM: unavailable";

            memoryElement.innerText = `${ramText} | ${processText} | ${heapText} | ${gpuText}`;
        } catch {
            memoryElement.innerText = "Memory: unavailable";
        }
    }

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
            if (chatHistory.length === 0) {
                renderActiveChatMessages();
            } else {
                messageElement.remove();
            }
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
        if (options.actions !== false) {
            addMessageActions(div);
        }
        return div;
    }

    function toggleInspector() {
        const inspector = document.getElementById('raw-inspector');
        const status = document.getElementById('inspector-status');
        if (inspector.style.display === 'block') {
            inspector.style.display = 'none';
            status.innerText = 'Collapsed';
        } else {
            inspector.style.display = 'block';
            status.innerText = 'Expanded';
        }
    }

    function handleKeyPress(e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendPrompt();
        }
    }

    function updateTempVal(val) {
        document.getElementById('tempValue').innerText = parseFloat(val).toFixed(1);
    }

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
            <span class="loading-text">Jarvis is thinking</span>
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

    function addToolMessage(message) {
        const chatBox = document.getElementById('chatBox');
        const toolDiv = createMessageElement('tool-call', message);
        chatBox.appendChild(toolDiv);
        chatBox.scrollTop = chatBox.scrollHeight;
    }

    async function fetchModelInfo(modelName) {
        const modelInfoDiv = document.getElementById('model-info');
        modelInfoDiv.innerHTML = 'Loading model info...';
        try {
            const response = await fetch(`${baseEndpoint}/v1/models/${encodeURIComponent(modelName)}`, { method: 'GET' });
            if (!response.ok) throw new Error();
            const data = await response.json();
            if (data.context_length && data.context_length > 0) {
                document.getElementById('contextSize').value = data.context_length;
                updateContextUsage();
            }
            modelInfoDiv.innerHTML = `
                <p><strong>ID:</strong> ${data.id || modelName}</p>
                <p><strong>Object:</strong> ${data.object || 'model'}</p>
                <p><strong>Owned By:</strong> ${data.owned_by || 'local'}</p>
                ${data.context_length ? `<p><strong>Context Length:</strong> ${formatNumber(data.context_length)}</p>` : ''}
                ${data.max_output_tokens > 0 ? `<p><strong>Max Output Tokens:</strong> ${formatNumber(data.max_output_tokens)}</p>` : ''}
                ${data.created ? `<p><strong>Created:</strong> ${new Date(data.created * 1000).toLocaleDateString()}</p>` : ''}
            `;
        } catch {
            modelInfoDiv.innerHTML = `<span class="model-info-empty">No additional metadata found for ${escapeHtml(modelName)}.</span>`;
        }
    }

    async function loadModels() {
        const modelSelect = document.getElementById('model');
        try {
            const response = await fetch(`${baseEndpoint}/v1/models`, { method: 'GET' });
            if (!response.ok) throw new Error();
            const data = await response.json();
            modelSelect.innerHTML = '';

            if (data.data && data.data.length > 0) {
                data.data.forEach(m => {
                    const opt = document.createElement('option');
                    opt.value = m.id; opt.innerText = m.id;
                    modelSelect.appendChild(opt);
                });
                fetchModelInfo(data.data[0].id);
            } else {
                throw new Error("No models returned");
            }
        } catch (err) {
            modelSelect.innerHTML = '<option value="qwen2.5-coder-14b">qwen2.5-coder-14b</option>';
            document.getElementById('statusText').innerText = 'Status: Fallback model loaded.';
            fetchModelInfo('qwen2.5-coder-14b');
        }

        modelSelect.addEventListener('change', (e) => {
            fetchModelInfo(e.target.value);
        });
    }

    async function sendPrompt() {
        const promptInput = document.getElementById('prompt');
        const chatBox = document.getElementById('chatBox');
        const userText = promptInput.value.trim();

        if (!userText) return;

        const userMessage = createChatHistoryMessage("user", userText);
        const userDiv = createMessageElement('user', userText, { historyId: userMessage._id });
        chatBox.appendChild(userDiv);

        promptInput.value = '';
        chatBox.scrollTop = chatBox.scrollHeight;

        chatHistory.push(userMessage);
        persistActiveChat();
        updateContextUsage();

        const lowerUserText = userText.toLowerCase();
        const needsSearch = lowerUserText.includes('search') || lowerUserText.includes('weather') || lowerUserText.includes('zoek') || lowerUserText.includes('weer');

        if (needsSearch && !alwaysAllowSearch) {
            askSearchPermission(userText);
        } else {
            executeAiRequest(needsSearch, userText);
        }
    }

    function askSearchPermission(query) {
        const chatBox = document.getElementById('chatBox');
        const statusText = document.getElementById('statusText');
        const submitBtn = document.getElementById('submitBtn');

        statusText.innerText = 'Status: Waiting for permission to use web_search...';
        submitBtn.disabled = true;

        const permissionId = 'perm-' + Date.now();

        const permDiv = document.createElement('div');
        permDiv.className = 'message tool-call permission-prompt';
        permDiv.id = permissionId;

        permDiv.innerHTML = `
            <p class="permission-text permission-title">🛡️ Permission required</p>
            <p class="permission-text">Jarvis wants to run <strong>web_search(query="${escapeHtml(query)}")</strong>. Do you approve?</p>
            <div class="permission-actions">
                <button class="permission-button yes" data-choice="yes">Yes</button>
                <button class="permission-button no" data-choice="no">No</button>
                <button class="permission-button always" data-choice="always">Always during this chat</button>
            </div>
        `;

        permDiv.querySelectorAll('.permission-button').forEach((button) => {
            button.addEventListener('click', () => {
                handlePermissionResponse(permissionId, button.dataset.choice, query);
            });
        });

        chatBox.appendChild(permDiv);
        chatBox.scrollTop = chatBox.scrollHeight;
    }

    function handlePermissionResponse(elementId, choice, query) {
        const permElement = document.getElementById(elementId);

        if (choice === 'always') {
            alwaysAllowSearch = true;
        }

        if (choice === 'yes' || choice === 'always') {
            permElement.innerHTML = `🔧 [TOOL CALL]: web_search(query="${escapeHtml(query)}") -> Status: Approved`;
            addMessageActions(permElement);
            executeAiRequest(true, query);
        } else {
            permElement.innerHTML = `❌ [TOOL CALL]: web_search(query="${escapeHtml(query)}") -> Status: Denied by user`;
            addMessageActions(permElement);
            executeAiRequest(false, query);
        }
    }

    async function executeAiRequest(searchExecuted, query) {
        const endpoint = document.getElementById('endpoint').value;
        const model = document.getElementById('model').value;
        const temp = parseFloat(document.getElementById('temperature').value);
        const contextSize = getContextSize();
        const chatBox = document.getElementById('chatBox');
        const statusText = document.getElementById('statusText');
        const submitBtn = document.getElementById('submitBtn');
        const rawReqPre = document.getElementById('raw-request');
        const rawStreamPre = document.getElementById('raw-stream');

        statusText.innerText = 'Status: Waiting for response...';
        submitBtn.disabled = true;

        currentAiBubbleElement = null;
        currentAiHistoryId = createMessageId();
        currentBubbleContentBuffer = "";
        showAssistantLoading();

        const requestBody = {
            model: model,
            messages: getRequestMessages(),
            temperature: temp,
            context_size: contextSize,
            stream: true,
            tools: JarvisTools.getOpenAiTools()
        };

        rawReqPre.innerText = JSON.stringify(requestBody, null, 2);
        rawStreamPre.innerText = '';

        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            if (!response.ok) throw new Error(`Status ${response.status}`);

            const reader = response.body.getReader();
            const decoder = new TextDecoder('utf-8');

            let buffer = '';
            let assistantFullContent = '';
            let pendingToolCall = null;

            while (true) {
                const { value, done } = await reader.read();
                if (done) break;

                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop();

                for (const line of lines) {
                    const cleaned = line.trim();
                    if (!cleaned) continue;

                    rawStreamPre.textContent += cleaned + '\n';
                    rawStreamPre.scrollTop = rawStreamPre.scrollHeight;

                    if (cleaned === 'data: [DONE]') {
                        statusText.innerText = 'Status: Stream complete.';
                        continue;
                    }

                    if (cleaned.startsWith('data: ')) {
                        try {
                            const parsed = JSON.parse(cleaned.replace('data: ', ''));
                            const delta = parsed.choices[0]?.delta;

                            if (!delta) continue;

                            if (delta.content) {
                                assistantFullContent += delta.content;
                                appendAssistantContent(delta.content);
                            }

                            if (delta.tool_calls && delta.tool_calls.length > 0) {
                                const tc = delta.tool_calls[0];
                                if (!pendingToolCall) {
                                    pendingToolCall = {
                                        id: tc.id,
                                        name: tc.function?.name,
                                        arguments: tc.function?.arguments || ""
                                    };
                                } else {
                                    if (tc.function?.arguments) {
                                        pendingToolCall.arguments += tc.function.arguments;
                                    }
                                }
                            }
                        } catch (e) {
                            console.error("Error parsing chunk:", e);
                        }
                    }
                }
            }

            if (pendingToolCall) {
                if (!assistantFullContent && currentAiBubbleElement) {
                    currentAiBubbleElement.remove();
                    currentAiBubbleElement = null;
                }

                statusText.innerText = `Status: Tool invoked (${pendingToolCall.name})`;

                let parsedArgs = {};
                try {
                    parsedArgs = JSON.parse(pendingToolCall.arguments);
                } catch {
                    parsedArgs = { query: pendingToolCall.arguments };
                }

                await JarvisTools.handleToolCall(pendingToolCall, parsedArgs, {
                    addToolMessage,
                    askSearchPermission,
                    executeAiRequest,
                    escapeHtml
                });
            } else {
                if (assistantFullContent) {
                    chatHistory.push({ _id: currentAiHistoryId, role: "assistant", content: assistantFullContent });
                    persistActiveChat();
                    updateContextUsage();
                    finalizeCurrentAssistantBubble(currentAiHistoryId);
                } else if (currentAiBubbleElement) {
                    currentAiBubbleElement.remove();
                    currentAiBubbleElement = null;
                }
            }

        } catch (error) {
            statusText.innerText = 'Status: An error occurred!';
            if (currentAiBubbleElement) {
                clearAssistantLoading();
                currentAiBubbleElement.querySelector('.message-content').textContent = `Error communicating with bridge: ${error.message}`;
                addMessageActions(currentAiBubbleElement);
            }
        } finally {
            submitBtn.disabled = false;
        }
    }
