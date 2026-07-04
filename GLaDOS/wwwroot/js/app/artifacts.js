    function parseArtifactEnvelope(content) {
        const artifacts = [];
        let text = typeof content === "string" ? content : getMessageContentText(content);

        if (Array.isArray(content)) {
            content.forEach((part) => addArtifactFromValue(part, artifacts));
            return { text, artifacts };
        }

        if (typeof content !== "string") {
            return { text, artifacts };
        }

        const trimmed = content.trim();
        if (!trimmed || (!trimmed.startsWith("{") && !trimmed.startsWith("["))) {
            return { text, artifacts };
        }

        try {
            const parsed = JSON.parse(trimmed);
            const parsedText = extractTextFromEnvelope(parsed);
            addArtifactFromValue(parsed, artifacts);

            if (artifacts.length > 0) {
                text = parsedText || "";
            }
        } catch {
            // Plain text that happens to start with a JSON-ish character.
        }

        return { text, artifacts };
    }

    function extractTextFromEnvelope(value) {
        if (!value || typeof value !== "object") return "";
        if (typeof value.text === "string") return value.text;
        if (typeof value.message === "string") return value.message;
        if (typeof value.caption === "string") return value.caption;
        if (typeof value.output_text === "string") return value.output_text;

        if (Array.isArray(value)) {
            return value.map(extractTextFromEnvelope).filter(Boolean).join("\n");
        }

        if (Array.isArray(value.content)) {
            return value.content.map(extractTextFromEnvelope).filter(Boolean).join("\n");
        }

        return "";
    }

    function addArtifactFromValue(value, artifacts) {
        if (!value) return;

        if (Array.isArray(value)) {
            value.forEach((item) => addArtifactFromValue(item, artifacts));
            return;
        }

        if (typeof value !== "object") return;

        ["artifacts", "files", "images", "attachments", "outputs", "output", "data"].forEach((key) => {
            if (Array.isArray(value[key])) {
                value[key].forEach((item) => addArtifactFromValue(item, artifacts));
            }
        });

        const nestedFile = value.file || value.image || value.image_url || value.input_image || value.output_image;
        if (nestedFile && typeof nestedFile === "object") {
            addArtifactFromValue({
                ...nestedFile,
                type: value.type || nestedFile.type,
                filename: value.filename || value.name || nestedFile.filename || nestedFile.name
            }, artifacts);
        }

        const artifact = normalizeArtifact(value);
        if (artifact) {
            artifacts.push(artifact);
        }
    }

    function normalizeArtifact(value) {
        const rawType = String(value.type || value.kind || "").toLowerCase();
        const mimeType = value.mime_type || value.mimeType || value.media_type || value.mediaType || value.content_type || value.contentType || "";
        const filename = sanitizeFilename(value.filename || value.file_name || value.name || value.title || defaultArtifactFilename(rawType, mimeType));
        const url = value.url || value.href || value.uri || null;
        const dataUrl = typeof url === "string" && url.startsWith("data:") ? url : null;
        const base64 = getArtifactBase64(value);
        const textContent = getArtifactTextContent(value);
        const isImage = rawType.includes("image") || mimeType.startsWith("image/") || rawType === "image_url" || rawType === "input_image" || rawType === "output_image";
        const isFile = rawType.includes("file") || rawType.includes("artifact") || Boolean(base64) || Boolean(dataUrl) || textContent !== null;

        if (!isImage && !isFile && !url) return null;
        if (!base64 && !dataUrl && !url && textContent === null) return null;

        return {
            type: isImage ? "image" : "file",
            filename,
            mimeType: mimeType || (isImage ? "image/png" : "application/octet-stream"),
            base64: base64 || null,
            textContent,
            url: dataUrl ? null : url,
            dataUrl,
            size: value.size || value.bytes_length || value.length || null
        };
    }

    function getArtifactBase64(value) {
        for (const key of artifactContentKeys) {
            const candidate = value[key];
            if (typeof candidate === "string") {
                if (candidate.startsWith("data:")) {
                    return candidate.slice(candidate.indexOf(",") + 1);
                }

                if (looksLikeBase64(candidate) || key !== "content") {
                    return candidate.replace(/\s/g, "");
                }
            }
        }

        const parts = value.parts || value.chunks || value.content_parts || value.contentBase64Parts;
        if (Array.isArray(parts) && parts.every((part) => typeof part === "string")) {
            return parts.join("").replace(/\s/g, "");
        }

        return null;
    }

    function getArtifactTextContent(value) {
        const rawType = String(value.type || value.kind || "").toLowerCase();
        const hasFileShape = rawType.includes("file") || rawType.includes("artifact") || value.filename || value.file_name;
        if (!hasFileShape) return null;

        const text = value.text_content || value.textContent || value.plain_text || value.plainText;
        if (typeof text === "string") return text;

        if (typeof value.content === "string" && !looksLikeBase64(value.content) && !value.content.startsWith("data:")) {
            return value.content;
        }

        return null;
    }

    function defaultArtifactFilename(type, mimeType) {
        const extension = mimeType.includes("/") ? mimeType.split("/")[1].split(";")[0] : "";
        const prefix = type.includes("image") ? "image" : "download";
        return extension ? `${prefix}.${extension}` : prefix;
    }

    function sanitizeFilename(filename) {
        const fallback = "download";
        return String(filename || fallback)
            .replace(/[\\/:*?"<>|]+/g, "_")
            .replace(/\s+/g, " ")
            .trim() || fallback;
    }

    function artifactToBlob(artifact) {
        if (artifact.dataUrl) {
            const [header, data = ""] = artifact.dataUrl.split(",", 2);
            const mimeMatch = /^data:([^;,]+)/.exec(header);
            return base64ToBlob(data, mimeMatch?.[1] || artifact.mimeType);
        }

        if (artifact.base64) {
            return base64ToBlob(artifact.base64, artifact.mimeType);
        }

        if (artifact.textContent !== null && artifact.textContent !== undefined) {
            return new Blob([artifact.textContent], { type: artifact.mimeType || "text/plain" });
        }

        return null;
    }

    function base64ToBlob(base64, mimeType) {
        const binary = atob(base64.replace(/\s/g, ""));
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i += 1) {
            bytes[i] = binary.charCodeAt(i);
        }

        return new Blob([bytes], { type: mimeType || "application/octet-stream" });
    }

    function getArtifactObjectUrl(artifact) {
        if (artifactObjectUrls.has(artifact)) return artifactObjectUrls.get(artifact);

        const blob = artifactToBlob(artifact);
        if (!blob) return artifact.url || "";

        const objectUrl = URL.createObjectURL(blob);
        artifactObjectUrls.set(artifact, objectUrl);
        return objectUrl;
    }

    function createArtifactListElement(artifacts) {
        const list = document.createElement("div");
        list.className = "artifact-list";

        artifacts.forEach((artifact) => {
            const item = document.createElement("div");
            item.className = `artifact-item ${artifact.type}`;

            if (artifact.type === "image") {
                const img = document.createElement("img");
                img.className = "artifact-image";
                img.alt = artifact.filename;
                img.loading = "lazy";
                img.src = getArtifactObjectUrl(artifact);
                item.appendChild(img);
            }

            const meta = document.createElement("div");
            meta.className = "artifact-meta";

            const name = document.createElement("div");
            name.className = "artifact-name";
            name.textContent = artifact.filename;

            const detail = document.createElement("div");
            detail.className = "artifact-detail";
            detail.textContent = artifact.mimeType || "download";

            const download = document.createElement("button");
            download.type = "button";
            download.className = "artifact-download";
            download.textContent = "Download";
            download.addEventListener("click", () => downloadArtifact(artifact));

            meta.append(name, detail, download);
            item.appendChild(meta);
            list.appendChild(item);
        });

        return list;
    }

    function downloadArtifact(artifact) {
        const href = getArtifactObjectUrl(artifact);
        if (!href) return;

        const link = document.createElement("a");
        link.href = href;
        link.download = artifact.filename;
        link.rel = "noopener";
        document.body.appendChild(link);
        link.click();
        link.remove();
    }
