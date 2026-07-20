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
                    messages: chat.messages
                        .filter((message) =>
                            message &&
                            typeof message._id === "string" &&
                            typeof message.role === "string" &&
                            (typeof message.content === "string" || Array.isArray(message.content)))
                        .map((message) => ({
                            ...message,
                            artifacts: Array.isArray(message.artifacts)
                                ? message.artifacts.map(({ objectUrl, ...artifact }) => artifact)
                                : undefined
                        }))
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
        alwaysAllowTools = new Set();
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

        const compactTitle = getMessageContentText(firstUserMessage.content).replace(/\s+/g, " ").trim();
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

        const deletedChatId = activeChatId;
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

        if (baseEndpoint) {
            void fetch(`${baseEndpoint}/v1/runtime/sessions/${encodeURIComponent(deletedChatId)}`, {
                method: 'DELETE'
            });
        }
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
            if (isToolCallCoveredByUiMessage(message)) return;
            chatBox.appendChild(createStoredMessageElement(message));
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
