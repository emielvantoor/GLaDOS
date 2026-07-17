    let availableModels = [];

    function getModelMetadata(modelName) {
        return availableModels.find(model => model.id === modelName) ?? null;
    }

    function getSelectedModelMaxOutputTokens() {
        const modelName = document.getElementById('model')?.value;
        const maxOutputTokens = getModelMetadata(modelName)?.max_output_tokens;
        return Number.isFinite(maxOutputTokens) && maxOutputTokens > 0 ? maxOutputTokens : null;
    }

    function getSelectedModelContextLength() {
        const modelName = document.getElementById('model')?.value;
        const contextLength = getModelMetadata(modelName)?.context_length;
        return Number.isFinite(contextLength) && contextLength > 0 ? contextLength : null;
    }

    function applySelectedModelDefaults(modelData) {
        if (!modelData) {
            return;
        }

        if (Number.isFinite(modelData.context_length) && modelData.context_length > 0) {
            const normalizedContextLength = Math.max(
                minContextSize,
                Math.floor(modelData.context_length / contextSizeStep) * contextSizeStep);
            document.getElementById('contextSize').value = normalizedContextLength;
        }

        if (Number.isFinite(modelData.max_output_tokens) && modelData.max_output_tokens > 0) {
            const normalizedMaxTokens = Math.max(
                minMaxCompletionTokens,
                Math.floor(modelData.max_output_tokens / maxCompletionTokensStep) * maxCompletionTokensStep);
            document.getElementById('maxCompletionTokens').value = normalizedMaxTokens;
        }
    }

    async function fetchModelInfo(modelName) {
        const modelInfoDiv = document.getElementById('model-info');
        modelInfoDiv.innerHTML = 'Loading model info...';
        try {
            const response = await fetch(`${baseEndpoint}/v1/models/${encodeURIComponent(modelName)}`, { method: 'GET' });
            if (!response.ok) throw new Error();
            const data = await response.json();
            applySelectedModelDefaults(data);
            updateTokenSettings();
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
            availableModels = Array.isArray(data.data) ? data.data : [];

            if (availableModels.length > 0) {
                availableModels.forEach(m => {
                    const opt = document.createElement('option');
                    opt.value = m.id; opt.innerText = m.id;
                    modelSelect.appendChild(opt);
                });
                applySelectedModelDefaults(availableModels[0]);
                updateTokenSettings();
                await fetchModelInfo(availableModels[0].id);
            } else {
                throw new Error("No models returned");
            }
        } catch (err) {
            availableModels = [];
            modelSelect.innerHTML = '<option value="qwen2.5-coder-14b">qwen2.5-coder-14b</option>';
            document.getElementById('statusText').innerText = 'Status: Fallback model loaded.';
            await fetchModelInfo('qwen2.5-coder-14b');
        }

        modelSelect.addEventListener('change', async (e) => {
            applySelectedModelDefaults(getModelMetadata(e.target.value));
            updateTokenSettings();
            await fetchModelInfo(e.target.value);
        });
    }
