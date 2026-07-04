    function escapeHtml(text) {
        if (!text) return "";
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function getMessageContentText(content) {
        if (typeof content === "string") return content;
        if (!Array.isArray(content)) return "";

        return content
            .map((part) => {
                if (typeof part === "string") return part;
                if (!part || typeof part !== "object") return "";
                if (typeof part.text === "string") return part.text;
                if (typeof part.content === "string" && !looksLikeBase64(part.content)) return part.content;
                return "";
            })
            .filter(Boolean)
            .join("\n");
    }

    function looksLikeBase64(value) {
        if (typeof value !== "string") return false;
        const compact = value.replace(/\s/g, "");
        return compact.length >= 64 && compact.length % 4 === 0 && /^[A-Za-z0-9+/]+={0,2}$/.test(compact);
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

    function initializeTextFileComposer() {
        const fileInput = document.getElementById('textFileInput');
        const attachButton = document.getElementById('attachTextBtn');
        const inputArea = document.querySelector('.input-area');
        const promptInput = document.getElementById('prompt');
        const statusText = document.getElementById('statusText');

        if (!fileInput || !attachButton || !inputArea || !promptInput) return;

        attachButton.addEventListener('click', () => fileInput.click());
        fileInput.addEventListener('change', async () => {
            await addTextFilesToPrompt(fileInput.files);
            fileInput.value = '';
        });

        ['dragenter', 'dragover'].forEach((eventName) => {
            inputArea.addEventListener(eventName, (event) => {
                if (!hasDraggedFiles(event)) return;
                event.preventDefault();
                inputArea.classList.add('drag-over');
            });
        });

        ['dragleave', 'drop'].forEach((eventName) => {
            inputArea.addEventListener(eventName, () => {
                inputArea.classList.remove('drag-over');
            });
        });

        inputArea.addEventListener('drop', async (event) => {
            if (!hasDraggedFiles(event)) return;
            event.preventDefault();
            await addTextFilesToPrompt(event.dataTransfer.files);
        });

        async function addTextFilesToPrompt(fileList) {
            const files = Array.from(fileList || []);
            const textFiles = files.filter(isReadableTextFile);
            const skipped = files.length - textFiles.length;

            if (!textFiles.length) {
                if (statusText) statusText.innerText = 'Status: No readable text files selected.';
                return;
            }

            if (statusText) statusText.innerText = `Status: Reading ${textFiles.length} text file${textFiles.length === 1 ? '' : 's'}...`;

            try {
                const sections = await Promise.all(textFiles.map(readFileAsPromptSection));
                appendToPrompt(sections.join('\n\n'));
                promptInput.focus();

                const skippedText = skipped ? ` ${skipped} non-text file${skipped === 1 ? '' : 's'} skipped.` : '';
                if (statusText) statusText.innerText = `Status: Added ${textFiles.length} text file${textFiles.length === 1 ? '' : 's'} to the chat input.${skippedText}`;
            } catch (error) {
                if (statusText) statusText.innerText = `Status: Could not read text file: ${error.message}`;
            }
        }

        function appendToPrompt(text) {
            const prefix = promptInput.value.trimEnd() ? '\n\n' : '';
            promptInput.value = `${promptInput.value.trimEnd()}${prefix}${text}`;
            promptInput.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }

    function hasDraggedFiles(event) {
        return Array.from(event.dataTransfer?.types || []).includes('Files');
    }

    function isReadableTextFile(file) {
        if (!file) return false;
        if (file.type && file.type.startsWith('text/')) return true;

        return /\.(txt|md|markdown|json|jsonl|csv|tsv|html?|css|js|jsx|ts|tsx|xml|ya?ml|ini|log|cs|fs|vb|sql|sh|ps1|bat|cmd|py|java|c|cpp|h|hpp|rs|go|php|rb)$/i.test(file.name || '');
    }

    function readFileAsPromptSection(file) {
        return file.text().then((text) => {
            const normalizedText = text.replace(/\r\n/g, '\n');
            return `File: ${file.name}\n\n${normalizedText}`;
        });
    }

    function initializeCollapsiblePanels() {
        document.querySelectorAll('.collapsible-panel').forEach((panel) => {
            const button = panel.querySelector('.panel-collapse-button');
            const content = panel.querySelector('.panel-content');
            if (!button || !content) return;

            const isCollapsed = panel.classList.contains('collapsed');
            button.setAttribute('aria-expanded', String(!isCollapsed));

            button.addEventListener('click', () => {
                const collapsed = panel.classList.toggle('collapsed');
                button.setAttribute('aria-expanded', String(!collapsed));
            });
        });
    }
