    let chatHistory = [];
    let nextMessageId = 1;
    let baseEndpoint = "";
    let alwaysAllowTools = new Set();
    const defaultContextSize = 32768;
    const chatsStorageKey = "jarvis-chats";
    const activeChatStorageKey = "jarvis-active-chat-id";
    const welcomeMessage = "Hello Emiel! I'm ready to assist you through your local hardware. Feel free to ask me anything!";
    let chats = [];
    let activeChatId = null;

    let currentAiBubbleElement = null;
    let currentAiHistoryId = null;
    let currentBubbleContentBuffer = "";
    let pendingPermissionPromptElement = null;
    let pendingPermissionPromptMessageId = null;
    const minContextSize = 1024;
    const maxContextSize = 32768;
    const contextSizeStep = 1024;
    const minMaxCompletionTokens = 256;
    const maxCompletionTokensStep = 256;
    const themeStorageKey = "jarvis-theme-mode";
    const themeModes = ["auto", "light", "dark"];
    const artifactContentKeys = [
        "data",
        "base64",
        "content",
        "file_data",
        "image_data",
        "b64_json",
        "bytes",
        "content_base64"
    ];
    const artifactObjectUrls = new WeakMap();
