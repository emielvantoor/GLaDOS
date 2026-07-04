(function () {
    const toolScripts = [
        "registry.js",
        // Add new tool files below. Keep registry.js first.
        "web-search.js",
        "execute-bash.js"
    ];

    const currentScript = document.currentScript;
    const baseUrl = currentScript.src.substring(0, currentScript.src.lastIndexOf("/") + 1);

    window.JarvisToolsReady = toolScripts.reduce((chain, scriptName) => {
        return chain.then(() => new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = `${baseUrl}${scriptName}`;
            script.onload = resolve;
            script.onerror = () => reject(new Error(`Failed to load tool script: ${scriptName}`));
            document.head.appendChild(script);
        }));
    }, Promise.resolve());
})();
