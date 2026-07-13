    function initializeAgentsView() {
        document.getElementById('chatTab')?.addEventListener('click', () => switchPrimaryView('chat'));
        document.getElementById('agentsTab')?.addEventListener('click', () => switchPrimaryView('agents'));
        document.getElementById('refreshAgentsBtn')?.addEventListener('click', () => refreshPotatoSessions({ forceDetail: true }));

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
            chatBox.appendChild(createMessageElement('assistant', 'Active Potato sessions will appear in the left pane.', { actions: false }));
            return;
        }

        const response = await fetch(`${baseEndpoint}/v1/potato/sessions/${encodeURIComponent(activePotatoSessionId)}`);
        if (!response.ok) return;

        const session = await response.json();
        setPotatoHeader(session);
        renderPotatoEvents(session.events || []);
    }

    function setPotatoHeader(session) {
        const title = document.getElementById('agentSessionTitle');
        const path = document.getElementById('agentSessionPath');
        const status = document.getElementById('agentSessionStatus');

        if (!session) {
            if (title) title.textContent = 'No Potato session selected';
            if (path) path.textContent = 'Start Potato from a working directory to mirror it here.';
            if (status) status.textContent = 'Idle';
            return;
        }

        if (title) title.textContent = session.displayName || 'Potato session';
        if (path) path.textContent = session.workingDirectory;
        if (status) status.textContent = session.status || 'active';
    }

    function renderPotatoEvents(events) {
        const chatBox = document.getElementById('agentChatBox');
        if (!chatBox) return;

        const previousCount = Number(chatBox.dataset.eventCount || '0');
        if (previousCount === events.length && chatBox.dataset.sessionId === activePotatoSessionId) {
            return;
        }

        chatBox.dataset.eventCount = String(events.length);
        chatBox.dataset.sessionId = activePotatoSessionId || '';
        chatBox.innerHTML = '';

        if (events.length === 0) {
            chatBox.appendChild(createMessageElement('assistant', 'Potato has not sent any events yet.', { actions: false }));
            return;
        }

        events.forEach((event) => {
            chatBox.appendChild(createPotatoEventElement(event));
        });

        chatBox.scrollTop = chatBox.scrollHeight;
        initializeCodeBlockActions();
    }

    function createPotatoEventElement(event) {
        if (event.collapsed) {
            const details = document.createElement('details');
            details.className = 'potato-event-details';

            const summary = document.createElement('summary');
            summary.textContent = `${event.kind || 'event'} · ${formatPotatoEventTime(event.timestamp)}`;

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

    function formatPotatoEventTime(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '';
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    }
