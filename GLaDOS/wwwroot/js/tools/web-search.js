(function () {
    const REQUEST_TIMEOUT_MS = 30000;
    const MAX_RESULT_LENGTH = 50000;

GLaDOSTools.register({
    name: "web_search",
    icon: "🌐",
    permitted: "User",
    schema: {
        name: "web_search",
        description: "Search on the internet or visit a page on the internet",
        parameters: {
            type: "object",
            properties: {
                query: {
                    type: "string",
                    description: "The search query or URL to fetch."
                }
            },
            required: ["query"]
        }
    },
    async handle(args, context) {
        const query = typeof args?.query === "string" ? args.query.trim() : "";

        if (!query) {
            await context.completeToolCall(
                { name: "web_search" },
                args,
                "Web search failed: query must be a non-empty string."
            );
            return;
        }

        context.addToolMessage(`🌐 Searching the web for: ${query}`);

        try {
            const requestUrl = buildRequestUrl(query);
            const controller = new AbortController();
            const timeout = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
            let response;

            try {
                response = await fetch(requestUrl, {
                    headers: { Accept: "text/plain" },
                    signal: controller.signal
                });
            } finally {
                window.clearTimeout(timeout);
            }

            if (!response.ok) {
                throw new Error(`HTTP ${response.status} ${response.statusText}`.trim());
            }

            const text = (await response.text()).trim();
            const output = truncateResult(text || "No results were returned.");
            await context.completeToolCall({ name: "web_search" }, { query }, output);
        } catch (error) {
            const reason = error.name === "AbortError"
                ? `request timed out after ${REQUEST_TIMEOUT_MS / 1000} seconds`
                : error.message;
            await context.completeToolCall(
                { name: "web_search" },
                { query },
                `Web search failed: ${reason}`
            );
        }
    }
});

function buildRequestUrl(query) {
    const pageUrl = parsePageUrl(query);
    return pageUrl
        ? `https://r.jina.ai/${pageUrl.href}`
        : `https://s.jina.ai/${encodeURIComponent(query)}`;
}

function parsePageUrl(value) {
    const candidate = /^https?:\/\//i.test(value) ? value : null;
    if (!candidate) return null;

    const url = new URL(candidate);
    if (url.protocol !== "http:" && url.protocol !== "https:") {
        return null;
    }

    return url;
}

function truncateResult(value) {
    if (value.length <= MAX_RESULT_LENGTH) return value;
    return `${value.slice(0, MAX_RESULT_LENGTH)}\n\n[Result truncated after ${MAX_RESULT_LENGTH} characters]`;
}
})();
