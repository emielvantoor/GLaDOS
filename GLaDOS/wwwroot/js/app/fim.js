    async function runFimVerification() {
        const model = document.getElementById('model').value;
        const prefixInput = document.getElementById('fimPrefix');
        const suffixInput = document.getElementById('fimSuffix');
        const output = document.getElementById('fimOutput');
        const status = document.getElementById('fimStatus');
        const runButton = document.getElementById('fimRunBtn');
        const rawReqPre = document.getElementById('raw-request');
        const rawStreamPre = document.getElementById('raw-stream');

        if (!model) {
            status.innerText = 'Select a model first.';
            return;
        }

        const requestBody = {
            model,
            prefix: prefixInput.value,
            suffix: suffixInput.value,
            temperature: parseFloat(document.getElementById('temperature').value),
            stream: false,
            stop: [
                ";",
                "\n}"
            ]
        };

        const maxCompletionTokens = getMaxCompletionTokens();
        if (maxCompletionTokens !== null) {
            requestBody.max_completion_tokens = Math.min(maxCompletionTokens, 32);
        }

        rawReqPre.innerText = JSON.stringify(requestBody, null, 2);
        rawStreamPre.innerText = '';
        output.innerText = '';
        status.innerText = 'Running FIM request...';
        runButton.disabled = true;

        try {
            const response = await fetch(`${baseEndpoint}/v1/fim/completions`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            const responseText = await response.text();
            rawStreamPre.innerText = responseText;

            if (!response.ok) {
                throw new Error(`Status ${response.status}: ${responseText}`);
            }

            const data = JSON.parse(responseText);
            const generatedText = data.choices?.[0]?.text ?? '';
            output.innerText = generatedText || '(empty completion)';
            status.innerText = isSuccessfulFimResult(generatedText)
                ? 'Succeeded: generated the expected infill.'
                : 'Failed: generated text did not match the expected infill.';
        } catch (error) {
            output.innerText = error instanceof Error ? error.message : String(error);
            status.innerText = 'FIM request failed.';
        } finally {
            runButton.disabled = false;
        }
    }

    function initializeFimVerification() {
        const runButton = document.getElementById('fimRunBtn');
        if (!runButton) return;

        runButton.addEventListener('click', runFimVerification);
    }

    function isSuccessfulFimResult(text) {
        return /\bleft\s*\+\s*right\b/.test(text);
    }
