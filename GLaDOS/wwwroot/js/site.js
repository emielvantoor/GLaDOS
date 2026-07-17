    window.addEventListener('DOMContentLoaded', async () => {
        if (window.GLaDOSToolsReady) {
            await window.GLaDOSToolsReady;
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
        document.getElementById('contextSize').addEventListener('input', updateTokenSettings);
        document.getElementById('maxCompletionTokens').addEventListener('input', updateMaxCompletionTokensValue);
        document.getElementById('inspectorToggle').addEventListener('click', toggleInspector);
        initializeTextFileComposer();
        initializeFimVerification();
        initializeAgentsView();
        initializeCollapsiblePanels();
        initializeCodeBlockActions();
        initializeChats();
        await GLaDOSTools.renderToolList(document.getElementById('tools-list'), { endpoint: baseEndpoint });
        await loadModels();
        updateTokenSettings();
        refreshRuntimeMemoryUsage();

        applyTheme();
        window.setInterval(applyTheme, 60 * 1000);
        window.setInterval(refreshRuntimeMemoryUsage, 5 * 1000);
    });
