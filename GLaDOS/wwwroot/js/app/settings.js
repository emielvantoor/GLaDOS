    function getContextSize() {
        const value = parseInt(document.getElementById('contextSize').value, 10);
        return clampToStep(value, minContextSize, getContextSizeLimit(), contextSizeStep, defaultContextSize);
    }

    function getMaxCompletionTokens() {
        const value = parseInt(document.getElementById('maxCompletionTokens').value, 10);
        return clampToStep(value, minMaxCompletionTokens, getMaxCompletionTokensLimit(), maxCompletionTokensStep, getMaxCompletionTokensLimit());
    }

    function clampToStep(value, min, max, step, fallback) {
        const safeValue = Number.isFinite(value) ? value : fallback;
        const clamped = Math.min(Math.max(safeValue, min), max);
        return Math.round(clamped / step) * step;
    }

    function getContextSizeLimit() {
        const modelContextLength = typeof getSelectedModelContextLength === 'function'
            ? getSelectedModelContextLength()
            : null;
        const normalizedModelLimit = Number.isFinite(modelContextLength) && modelContextLength > 0
            ? Math.floor(modelContextLength / contextSizeStep) * contextSizeStep
            : null;
        return Math.max(minContextSize, normalizedModelLimit ?? maxContextSize);
    }

    function getMaxCompletionTokensLimit(contextSize = getContextSize()) {
        const contextBasedLimit = Math.min(8192, Math.floor(contextSize / 4));
        const modelBasedLimit = typeof getSelectedModelMaxOutputTokens === 'function'
            ? getSelectedModelMaxOutputTokens()
            : null;
        const practicalLimit = Number.isFinite(modelBasedLimit) && modelBasedLimit > 0
            ? Math.min(modelBasedLimit, contextBasedLimit)
            : contextBasedLimit;
        return Math.max(minMaxCompletionTokens, Math.floor(practicalLimit / maxCompletionTokensStep) * maxCompletionTokensStep);
    }

    function updateTokenSettings() {
        const contextInput = document.getElementById('contextSize');
        const contextSizeLimit = getContextSizeLimit();
        contextInput.max = contextSizeLimit;
        const contextSize = getContextSize();
        const maxCompletionInput = document.getElementById('maxCompletionTokens');
        const maxCompletionLimit = getMaxCompletionTokensLimit(contextSize);

        contextInput.value = contextSize;
        document.getElementById('contextSizeValue').innerText = formatNumber(contextSize);

        maxCompletionInput.max = maxCompletionLimit;
        if (getMaxCompletionTokens() > maxCompletionLimit) {
            maxCompletionInput.value = maxCompletionLimit;
        }

        updateMaxCompletionTokensValue();
        updateContextUsage();
    }

    function updateMaxCompletionTokensValue() {
        const maxCompletionTokens = getMaxCompletionTokens();
        document.getElementById('maxCompletionTokens').value = maxCompletionTokens;
        document.getElementById('maxCompletionTokensValue').innerText = formatNumber(maxCompletionTokens);
    }

    function estimateTokenCount(text) {
        if (!text) return 0;
        return Math.ceil(text.length / 4);
    }

    function getChatContextUsage() {
        return getRequestMessages().reduce((total, message) => {
            return total + estimateTokenCount(`${message.role}\n${getMessageContentText(message.content)}`);
        }, 0);
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
            const processText = `GLaDOS: ${formatMegabytes(data.process_ram_mb)} MB`;
            const heapText = `Heap: ${formatMegabytes(data.managed_heap_mb)} MB`;
            const gpuText = data.gpu_vram_total_mb
                ? `VRAM: ${formatMegabytes(data.gpu_vram_used_mb)} / ${formatMegabytes(data.gpu_vram_total_mb)} MB${data.gpu_name ? ` (${data.gpu_name})` : ""}`
                : "VRAM: unavailable";

            memoryElement.innerText = `${ramText} | ${processText} | ${heapText} | ${gpuText}`;
        } catch {
            memoryElement.innerText = "Memory: unavailable";
        }
    }
