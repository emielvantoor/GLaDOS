GLaDOSTools.register({
    name: "execute_bash",
    icon: "💻",
    permitted: "User",
    schema: {
        name: "execute_bash",
        description: "Execute a local bash command",
        parameters: {
            type: "object",
            properties: {
                command: {
                    type: "string",
                    description: "The bash command line to run."
                }
            },
            required: ["command"]
        }
    },
    handle(args, context) {
        context.addToolMessage(`💻 Model wants to run bash: ${args.command || ""}`);
    }
});
