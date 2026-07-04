    async function fetchModelInfo(modelName) {
        const modelInfoDiv = document.getElementById('model-info');
        modelInfoDiv.innerHTML = 'Loading model info...';
        try {
            const response = await fetch(`${baseEndpoint}/v1/models/${encodeURIComponent(modelName)}`, { method: 'GET' });
            if (!response.ok) throw new Error();
            const data = await response.json();
            if (data.context_length && data.context_length > 0) {
                document.getElementById('contextSize').value = data.context_length;
                updateTokenSettings();
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
