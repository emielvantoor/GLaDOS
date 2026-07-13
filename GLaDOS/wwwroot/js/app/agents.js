    let agentCompletionItems = [];
    let agentCompletionIndex = 0;
    let agentCompletionRequestId = 0;
    let agentCompletionTimer = null;

    function initializeAgentsView() {
        document.getElementById('chatTab')?.addEventListener('click', () => switchPrimaryView('chat'));
        document.getElementById('agentsTab')?.addEventListener('click', () => switchPrimaryView('agents'));
        document.getElementById('refreshAgentsBtn')?.addEventListener('click', () => refreshPotatoSessions({ forceDetail: true }));
        document.getElementById('agentSubmitBtn')?.addEventListener('click', sendPotatoInput);
        document.getElementById('agentPrompt')?.addEventListener('keydown', handleAgentPromptKeyPress);
        document.getElementById('agentPrompt')?.addEventListener('input', scheduleAgentCompletions);
        document.getElementById('agentPrompt')?.addEventListener('click', scheduleAgentCompletions);
        setAgentComposerState(false, 'Status: Select a Potato session');

        refreshPotatoSessions();
        potatoSessionsPollId = window.setInterval(refreshPotatoSessions, 2000);
    }

    function switchPrimaryView(view) {
        activePrimaryView = view === 'agents' ? 'agents' : 'chat';

        const isAgents = activePrimaryView === 'agents';
        document.getElementById('chatTab')?.classList.toggle('active', !isAgents);
        document.getElementById('chatTab')?.setAttribute('aria-selected', String(!isAgents));
        document.getElementById('agentsTab')?.classList.toggle('active', isAgents);
        document.getElementById('agentsTab')?.setAttribute('aria-selected', String(isAgents));
        document.querySelector('.chat-main')?.classList.toggle('hidden', isAgents);
        document.getElementById('chatSidebar')?.classList.toggle('hidden', isAgents);
        document.getElementById('agentMain')?.classList.toggle('hidden', !isAgents);
        document.getElementById('agentsSidebar')?.classList.toggle('hidden', !isAgents);
        document.getElementById('inspectorToggle')?.classList.toggle('hidden', isAgents);
        document.getElementById('raw-inspector')?.classList.toggle('hidden', isAgents);

        if (isAgents) {
            refreshPotatoSessions({ forceDetail: true });
        }
    }

    async function refreshPotatoSessions(options = {}) {
        if (!baseEndpoint) return;

        try {
            const response = await fetch(`${baseEndpoint}/v1/potato/sessions`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const payload = await response.json();
            potatoSessions = Array.isArray(payload.data) ? payload.data : [];

            if (activePotatoSessionId && !potatoSessions.some((session) => session.id === activePotatoSessionId)) {
                activePotatoSessionId = potatoSessions[0]?.id || null;
            } else if (!activePotatoSessionId && potatoSessions.length > 0) {
                activePotatoSessionId = potatoSessions[0].id;
            }

            renderPotatoSessionList();

            if (activePrimaryView === 'agents' || options.forceDetail) {
                await refreshActivePotatoSession();
            }
        } catch (error) {
            const empty = document.getElementById('potatoSessionsEmpty');
            if (empty) empty.textContent = `Could not load Potato sessions: ${error.message}`;
        }
    }

    function renderPotatoSessionList() {
        const list = document.getElementById('potatoSessionList');
        const empty = document.getElementById('potatoSessionsEmpty');
        if (!list || !empty) return;

        list.innerHTML = '';
        empty.style.display = potatoSessions.length ? 'none' : 'block';
        empty.textContent = 'No active Potato sessions.';

        potatoSessions.forEach((session) => {
            const item = document.createElement('button');
            item.type = 'button';
            item.className = `chat-list-item${session.id === activePotatoSessionId ? ' active' : ''}`;
            item.title = session.workingDirectory;
            item.addEventListener('click', async () => {
                activePotatoSessionId = session.id;
                renderPotatoSessionList();
                await refreshActivePotatoSession();
            });

            const title = document.createElement('div');
            title.className = 'chat-list-title';
            title.textContent = session.displayName || session.workingDirectory;

            const meta = document.createElement('div');
            meta.className = 'chat-list-meta';
            meta.textContent = `${session.messageCount} events · ${session.model || 'model unknown'}`;

            item.append(title, meta);
            list.appendChild(item);
        });
    }

    async function refreshActivePotatoSession() {
        const chatBox = document.getElementById('agentChatBox');
        if (!chatBox) return;

        if (!activePotatoSessionId) {
            setPotatoHeader(null);
            chatBox.innerHTML = '';
            chatBox.dataset.eventCount = '0';
            chatBox.dataset.lastSequence = '0';
            chatBox.dataset.sessionId = '';
            chatBox.appendChild(createMessageElement('assistant', 'Active Potato sessions will appear in the left pane.', { actions: false }));
            updatePotatoThinkingIndicator(null);
            return;
        }

        const response = await fetch(`${baseEndpoint}/v1/potato/sessions/${encodeURIComponent(activePotatoSessionId)}`);
        if (!response.ok) return;

        const session = await response.json();
        setPotatoHeader(session);
        renderPotatoEvents(session.events || []);
        updatePotatoThinkingIndicator(session);
    }

    function setPotatoHeader(session) {
        const title = document.getElementById('agentSessionTitle');
        const path = document.getElementById('agentSessionPath');
        const status = document.getElementById('agentSessionStatus');

        if (!session) {
            if (title) title.textContent = 'No Potato session selected';
            if (path) path.textContent = 'Start Potato from a working directory to mirror it here.';
            if (status) status.textContent = 'Idle';
            setAgentComposerState(false, 'Status: Select a Potato session');
            hideAgentCompletions();
            return;
        }

        if (title) title.textContent = session.displayName || 'Potato session';
        if (path) path.textContent = session.workingDirectory;
        if (status) status.textContent = session.isProcessing ? 'thinking' : (session.status || 'active');
        setAgentComposerState(true, session.isProcessing ? 'Status: Potato is thinking' : 'Status: Ready');
        scheduleAgentCompletions();
    }

    function renderPotatoEvents(events) {
        const chatBox = document.getElementById('agentChatBox');
        if (!chatBox) return;

        if (chatBox.dataset.sessionId !== activePotatoSessionId) {
            chatBox.dataset.eventCount = '0';
            chatBox.dataset.lastSequence = '0';
            chatBox.dataset.sessionId = activePotatoSessionId || '';
            chatBox.innerHTML = '';
        }

        const previousCount = Number(chatBox.dataset.eventCount || '0');
        const lastSequence = Number(chatBox.dataset.lastSequence || '0');
        if (previousCount === events.length) {
            return;
        }

        chatBox.dataset.eventCount = String(events.length);

        if (events.length === 0) {
            chatBox.dataset.lastSequence = '0';
            chatBox.innerHTML = '';
            chatBox.appendChild(createMessageElement('assistant', 'Potato has not sent any events yet.', { actions: false }));
            return;
        }

        const newEvents = events.filter((event) => Number(event.sequence || 0) > lastSequence);
        if (events.length < previousCount || (events.length > previousCount && newEvents.length === 0)) {
            chatBox.innerHTML = '';
            chatBox.dataset.lastSequence = '0';
            events.forEach((event) => {
                chatBox.appendChild(createPotatoEventElement(event));
            });
            chatBox.dataset.lastSequence = String(Math.max(...events.map((event) => Number(event.sequence || 0))));
            chatBox.scrollTop = chatBox.scrollHeight;
            initializeCodeBlockActions();
            return;
        }

        const shouldStickToBottom = isScrolledNearBottom(chatBox);
        newEvents.forEach((event) => {
            chatBox.appendChild(createPotatoEventElement(event));
        });

        chatBox.dataset.lastSequence = String(Math.max(lastSequence, ...events.map((event) => Number(event.sequence || 0))));
        if (shouldStickToBottom) {
            chatBox.scrollTop = chatBox.scrollHeight;
        }

        initializeCodeBlockActions();
    }

    function isScrolledNearBottom(element) {
        return element.scrollHeight - element.scrollTop - element.clientHeight < 80;
    }

    function updatePotatoThinkingIndicator(session) {
        const chatBox = document.getElementById('agentChatBox');
        if (!chatBox) return;

        const existing = chatBox.querySelector('.potato-thinking-indicator');
        if (!session?.isProcessing) {
            existing?.remove();
            return;
        }

        const progressText = session.currentProgress || 'Potato is thinking';
        const shouldStickToBottom = isScrolledNearBottom(chatBox);
        const indicator = existing || createPotatoThinkingIndicator();
        indicator.querySelector('.loading-text').textContent = progressText;

        if (!existing) {
            chatBox.appendChild(indicator);
        }

        if (shouldStickToBottom) {
            chatBox.scrollTop = chatBox.scrollHeight;
        }
    }

    function createPotatoThinkingIndicator() {
        const div = document.createElement('div');
        div.className = 'message assistant loading potato-thinking-indicator';
        div.setAttribute('aria-live', 'polite');

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';
        contentDiv.innerHTML = `
            <span class="loading-spinner" aria-hidden="true"></span>
            <span class="loading-text">Potato is thinking</span>
        `;
        div.appendChild(contentDiv);
        return div;
    }

    function createPotatoEventElement(event) {
        if (event.kind === 'permission') {
            return createPotatoPermissionElement(event);
        }

        if (event.collapsed) {
            const details = document.createElement('details');
            details.className = 'potato-event-details';

            const summary = document.createElement('summary');
            summary.textContent = `${formatPotatoEventSummary(event)} · ${formatPotatoEventTime(event.timestamp)}`;

            const content = document.createElement('pre');
            content.textContent = event.content || '';

            details.append(summary, content);
            return details;
        }

        const role = event.role === 'user' ? 'user' : 'assistant';
        const content = event.role === 'assistant'
            ? formatAssistantMessage(event.content || '')
            : (event.content || '');

        return createMessageElement(role, content, {
            html: event.role === 'assistant',
            actions: false
        });
    }

    function createPotatoPermissionElement(event) {
        const permDiv = document.createElement('div');
        permDiv.className = 'message tool-call permission-prompt potato-permission-prompt';

        const title = document.createElement('p');
        title.className = 'permission-text permission-title';
        title.textContent = 'Permission required';

        const content = document.createElement('pre');
        content.className = 'potato-permission-content';
        content.textContent = event.content || 'Potato requested permission.';

        const actions = document.createElement('div');
        actions.className = 'permission-actions';

        [
            { label: 'Once', value: 'once', className: 'yes' },
            { label: 'Always', value: 'always', className: 'always' },
            { label: 'Deny', value: 'deny', className: 'no' }
        ].forEach((choice) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = `permission-button ${choice.className}`;
            button.textContent = choice.label;
            button.addEventListener('click', () => sendPotatoPermissionChoice(choice.value, permDiv));
            actions.appendChild(button);
        });

        permDiv.append(title, content, actions);
        return permDiv;
    }

    async function sendPotatoPermissionChoice(choice, promptElement) {
        if (!activePotatoSessionId) return;

        promptElement.querySelectorAll('button').forEach((button) => {
            button.disabled = true;
        });
        setAgentComposerState(false, `Status: Sending permission choice (${choice})...`);

        try {
            const response = await fetch(`${baseEndpoint}/v1/potato/sessions/${encodeURIComponent(activePotatoSessionId)}/input`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: choice })
            });

            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            promptElement.classList.add('permission-choice-submitted');
            setAgentComposerState(true, `Status: Permission choice sent (${choice})`);
            await refreshActivePotatoSession();
        } catch (error) {
            promptElement.querySelectorAll('button').forEach((button) => {
                button.disabled = false;
            });
            setAgentComposerState(true, `Status: Could not send permission choice: ${error.message}`);
        }
    }

    function formatPotatoEventTime(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '';
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    }

    function formatPotatoEventSummary(event) {
        const kind = event.kind || 'event';
        if (kind === 'model-exchange') return getPotatoEventStepTitle(event.content) || 'step: model exchange';
        if (kind === 'model-request') return 'Potato model request';
        if (kind === 'model-response') return 'Potato model response';
        if (kind === 'progress') return `step: ${event.content || 'progress'}`;
        if (kind === 'input') return 'Queued browser input';
        if (kind === 'shortcuts') return 'Potato shortcuts';
        return kind;
    }

    function getPotatoEventStepTitle(content) {
        const firstLine = (content || '').split(/\r?\n/, 1)[0]?.trim();
        return firstLine?.toLowerCase().startsWith('step: ') ? firstLine : '';
    }

    function handleAgentPromptKeyPress(event) {
        if (isAgentCompletionsVisible()) {
            if (event.key === 'ArrowDown') {
                event.preventDefault();
                selectAgentCompletion(agentCompletionIndex + 1);
                return;
            }

            if (event.key === 'ArrowUp') {
                event.preventDefault();
                selectAgentCompletion(agentCompletionIndex - 1);
                return;
            }

            if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey)) {
                event.preventDefault();
                applyAgentCompletion(agentCompletionItems[agentCompletionIndex]);
                return;
            }

            if (event.key === 'Escape') {
                event.preventDefault();
                hideAgentCompletions();
                return;
            }
        }

        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            sendPotatoInput();
        }
    }

    async function sendPotatoInput() {
        const promptInput = document.getElementById('agentPrompt');
        const userText = promptInput?.value.trim();
        if (!promptInput || !userText || !activePotatoSessionId) return;

        setAgentComposerState(false, 'Status: Sending input...');

        try {
            const response = await fetch(`${baseEndpoint}/v1/potato/sessions/${encodeURIComponent(activePotatoSessionId)}/input`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content: userText })
            });

            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            promptInput.value = '';
            hideAgentCompletions();
            setAgentComposerState(true, 'Status: Input queued');
            await refreshActivePotatoSession();
        } catch (error) {
            setAgentComposerState(true, `Status: Could not send input: ${error.message}`);
        }
    }

    function setAgentComposerState(enabled, statusText) {
        const promptInput = document.getElementById('agentPrompt');
        const submitButton = document.getElementById('agentSubmitBtn');
        const status = document.getElementById('agentStatusText');

        if (promptInput) promptInput.disabled = !enabled;
        if (submitButton) submitButton.disabled = !enabled;
        if (status) status.textContent = statusText;
        if (!enabled) hideAgentCompletions();
    }

    function scheduleAgentCompletions() {
        window.clearTimeout(agentCompletionTimer);
        agentCompletionTimer = window.setTimeout(updateAgentCompletions, 80);
    }

    async function updateAgentCompletions() {
        const promptInput = document.getElementById('agentPrompt');
        if (!promptInput || promptInput.disabled || !activePotatoSessionId) {
            hideAgentCompletions();
            return;
        }

        const content = promptInput.value;
        const cursorIndex = promptInput.selectionStart ?? content.length;
        if (!shouldRequestAgentCompletions(content, cursorIndex)) {
            hideAgentCompletions();
            return;
        }

        const requestId = ++agentCompletionRequestId;
        try {
            const response = await fetch(`${baseEndpoint}/v1/potato/sessions/${encodeURIComponent(activePotatoSessionId)}/completions`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ content, cursorIndex })
            });

            if (requestId !== agentCompletionRequestId) return;
            if (!response.ok) {
                hideAgentCompletions();
                return;
            }

            const payload = await response.json();
            renderAgentCompletions(Array.isArray(payload.data) ? payload.data : []);
        } catch {
            if (requestId === agentCompletionRequestId) hideAgentCompletions();
        }
    }

    function shouldRequestAgentCompletions(content, cursorIndex) {
        if (cursorIndex !== content.length) return false;
        const text = content.slice(0, cursorIndex);
        if (text.startsWith('/') && !text.includes('\n')) return true;
        return /(^|\s)@\S*$/.test(text);
    }

    function renderAgentCompletions(completions) {
        const container = document.getElementById('agentCompletions');
        if (!container) return;

        agentCompletionItems = completions;
        agentCompletionIndex = 0;
        container.innerHTML = '';

        if (completions.length === 0) {
            hideAgentCompletions();
            return;
        }

        completions.forEach((completion, index) => {
            const item = document.createElement('button');
            item.type = 'button';
            item.className = `agent-completion-item${index === agentCompletionIndex ? ' active' : ''}`;
            item.addEventListener('mousedown', (event) => {
                event.preventDefault();
                applyAgentCompletion(completion);
            });

            const label = document.createElement('span');
            label.className = 'agent-completion-label';
            label.textContent = completion.replacementText || completion.displayText || '';

            const kind = document.createElement('span');
            kind.className = 'agent-completion-kind';
            kind.textContent = completion.kind || 'completion';

            item.append(label, kind);
            container.appendChild(item);
        });

        container.classList.remove('hidden');
    }

    function selectAgentCompletion(index) {
        if (agentCompletionItems.length === 0) return;
        agentCompletionIndex = ((index % agentCompletionItems.length) + agentCompletionItems.length) % agentCompletionItems.length;
        document.querySelectorAll('.agent-completion-item').forEach((item, itemIndex) => {
            item.classList.toggle('active', itemIndex === agentCompletionIndex);
        });
    }

    function applyAgentCompletion(completion) {
        const promptInput = document.getElementById('agentPrompt');
        if (!promptInput || !completion) return;

        const content = promptInput.value;
        const cursorIndex = promptInput.selectionStart ?? content.length;
        const replacementStart = Math.max(0, Math.min(completion.replacementStart ?? cursorIndex, cursorIndex));
        const replacementText = completion.replacementText || '';
        promptInput.value = content.slice(0, replacementStart) + replacementText + content.slice(cursorIndex);
        const nextCursorIndex = replacementStart + replacementText.length;
        promptInput.setSelectionRange(nextCursorIndex, nextCursorIndex);
        promptInput.focus();
        hideAgentCompletions();
        scheduleAgentCompletions();
    }

    function hideAgentCompletions() {
        const container = document.getElementById('agentCompletions');
        if (container) {
            container.classList.add('hidden');
            container.innerHTML = '';
        }

        agentCompletionItems = [];
        agentCompletionIndex = 0;
    }

    function isAgentCompletionsVisible() {
        const container = document.getElementById('agentCompletions');
        return !!container && !container.classList.contains('hidden') && agentCompletionItems.length > 0;
    }
