JarvisTools.register({
    name: "web_search",
    icon: "🛠️",
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
    handle(args, context) {
        context.addToolMessage(`🛠️ web_search(query="${args.query || "nu.nl"}") approved.`);
    }
});
