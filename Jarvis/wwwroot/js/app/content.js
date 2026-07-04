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
