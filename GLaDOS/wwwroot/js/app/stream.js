    async function executeAiRequest() {
        const endpoint = document.getElementById('endpoint').value;
        const model = document.getElementById('model').value;
        const temp = parseFloat(document.getElementById('temperature').value);
        const contextSize = getContextSize();
        const maxCompletionTokens = getMaxCompletionTokens();
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
            tools: GLaDOSTools.getOpenAiTools()
        };

        if (maxCompletionTokens !== null) {
            requestBody.max_completion_tokens = maxCompletionTokens;
        }

        rawReqPre.innerText = JSON.stringify(requestBody, null, 2);
        rawStreamPre.innerText = '';

        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-GLaDOS-Session-Id': activeChatId || 'default'
                },
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
                                const deltaText = typeof delta.content === "string"
                                    ? delta.content
                                    : getMessageContentText(delta.content);
                                assistantFullContent += deltaText;
                                appendAssistantContent(deltaText);
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

                await GLaDOSTools.handleToolCall(pendingToolCall, parsedArgs, {
                    addToolMessage,
                    requestToolPermission,
                    executeInternalTool,
                    completeToolCall,
                    executeAiRequest,
                    escapeHtml
                });
            } else {
                if (assistantFullContent) {
                    const envelope = parseArtifactEnvelope(assistantFullContent);
                    const assistantMessage = {
                        _id: currentAiHistoryId,
                        role: "assistant",
                        content: assistantFullContent
                    };
                    if (envelope.artifacts.length > 0) {
                        assistantMessage.artifacts = envelope.artifacts;
                        currentAiBubbleElement.querySelector('.message-content').innerHTML = formatAssistantMessage(envelope.text);
                        currentAiBubbleElement.appendChild(createArtifactListElement(envelope.artifacts));
                    }

                    chatHistory.push(assistantMessage);
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
            await refreshServerContextUsage();
        }
    }
