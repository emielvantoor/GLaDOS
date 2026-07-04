JarvisTools.register({
    name: "web_search",
    icon: "🛠️",
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
        context.askSearchPermission(args.query || "nu.nl");
    }
});
