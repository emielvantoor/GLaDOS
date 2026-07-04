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

        executeAiRequest();
    }
