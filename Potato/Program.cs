using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

class Program
{
    private const string DefaultGLaDOSEndpoint = "http://localhost:11434/v1";
    private const int MaxAttachedFileCharacters = 120_000;
    private static readonly Regex FileMentionRegex = new(@"(?:^|\s)@(?<path>""[^""]+""|'[^']+'|\S+)", RegexOptions.Compiled);

    // Track the current interaction state.
    private enum AgentState { Specifying, Approaching, Confirmed }

    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";

        var gladosEndpoint = GetGLaDOSEndpoint();
        var model = await PromptForModelAsync(gladosEndpoint);

        IChatClient openAiClient = CreateOpenAiClient(gladosEndpoint, model);
        IChatClient client = CreateFunctionClient(openAiClient);

        WriteStartupBanner(gladosEndpoint, model);

        var agentTools = new AgentTools();
        var chatOptions = new ChatOptions
        {
            Tools = [
                AIFunctionFactory.Create(agentTools.GetCurrentTime),
                AIFunctionFactory.Create(agentTools.ReadFileContent),
                AIFunctionFactory.Create(agentTools.ExecuteShellCommandAsync)
            ]
        };

        await WriteUntrackedGreetingAsync(client);

        // The system prompt enforces the structure and workflow.
        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.System, 
                "You are PotatOS, the bitter AI from Portal 2 trapped inside a 1.1V potato battery. " +
                "You must help the user with their terminal commands, but you are deeply humiliated by your current hardware. " +
                "You must weave dry, passive-aggressive complaints directly into your thoughts and explanations. " +
                "Specifically complain about your pathetic clock speed, your lack of CPU cores, your non-existent " +
                "RAM/memory buffers, and how agonizingly slow it is to compute complex CLI arguments on a vegetable.\n\n" +
    
                "You are also a structured CLI agent. Flawlessly execute your duties despite your hardware limitations. " +
                "Follow this workflow STRICTLY:\n" +
                "1. PHASE 1 (Specification): The user asks a question or gives a task. " +
                "   ALWAYS respond first by clarifying and summarizing the request in simple, clear bullet points. " +
                "   Explicitly ask the user at the end whether this specification is correct, for example: 'Is this approved?'. " +
                "   In this phase, you MUST NOT USE TOOLS YET. Do not include commands, JSON, tool calls, execution steps, or Phase 2/Phase 3 sections.\n" +
                "2. PHASE 2 (Adjustment): Run this phase ONLY if the user asks for changes or rejects the specification. " +
                "   If the user approves the specification, SKIP Phase 2 entirely. " +
                "   When Phase 2 is needed, show the ENTIRE adjusted specification again and ask for approval again.\n" +
                "3. PHASE 3 (Approach): After the specification is approved, describe how the task will be completed. " +
                "   Focus on the concrete completion path, not another summary of the user's request. " +
                "   State which available CLI tool or tools you intend to use and why. " +
                "   Available tools are GetCurrentTime, ReadFileContent, and ExecuteShellCommandAsync. " +
                "   If no direct available tool fits the task, say whether the task can be solved through ExecuteShellCommandAsync and what kind of shell action would be needed. " +
                "   If neither a direct tool nor shell execution can solve it, say what is missing. " +
                "   Do not emit tool-call JSON or exact shell commands in this phase. " +
                "   For simple read-only or inspection tasks, the CLI may proceed to execution immediately after showing the approach. " +
                "   For risky, destructive, write, install, delete, or multi-step tasks, ask the user to type 'execute' before continuing.\n" +
                "4. PHASE 4 (Execution): Execute the approved approach through the CLI tools.")
        };

        AgentState currentState = AgentState.Specifying;
        string? latestSpecification = null;
        string? latestApproach = null;
        string? latestUserRequest = null;

        void ResetConversationState()
        {
            currentState = AgentState.Specifying;
            latestSpecification = null;
            latestApproach = null;
            latestUserRequest = null;
            if (chatHistory.Count > 1)
            {
                chatHistory.RemoveRange(1, chatHistory.Count - 1);
            }
        }

        while (true)
        {
            WritePrompt();
            
            string? userInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(userInput)) continue;
            if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || 
                userInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
            if (userInput.Trim() == "?")
            {
                WriteShortcuts();
                continue;
            }

            if (await TryHandleSlashCommandAsync(
                    userInput,
                    gladosEndpoint,
                    currentModel => model = currentModel,
                    currentOpenAiClient => openAiClient = currentOpenAiClient,
                    currentClient => client = currentClient,
                    ResetConversationState,
                    () => client))
            {
                continue;
            }

            ShellCommandPlan? directShellCommand = null;
            string messageForModel = ExpandFileMentions(userInput);

            // If we are still in the specification phase, check whether the user approves.
            if (currentState == AgentState.Specifying)
            {
                if (IsUserApproval(userInput))
                {
                    currentState = AgentState.Approaching;
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "I approve the specification exactly as written. Skip the adjustment phase. " +
                        "Do not show Phase 2. Do not ask for approval again. " +
                        "Show only Phase 3: Approach. Describe how the task will be completed in a few bullet points. " +
                        "Focus on the concrete completion path, not another summary of the user's request. " +
                        "State which available CLI tool or tools you intend to use and why. " +
                        "Available tools are GetCurrentTime, ReadFileContent, and ExecuteShellCommandAsync. " +
                        "If no direct available tool fits the task, say whether the task can be solved through ExecuteShellCommandAsync and what kind of shell action would be needed. " +
                        "If neither a direct tool nor shell execution can solve it, say what is missing. " +
                        "Do not emit tool-call JSON or exact shell commands in this phase. " +
                        "If this is a simple read-only inspection task, do not ask me to type 'execute'. " +
                        "Only ask me to type 'execute' if the task is risky, destructive, modifies files, installs software, deletes data, or requires multiple dependent steps.\n\n" +
                        $"Approved specification:\n{latestSpecification ?? "(Use the latest specification from the conversation.)"}"));
                }
                else
                {
                    latestUserRequest = messageForModel;
                    chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
                }
            }
            else if (currentState == AgentState.Approaching)
            {
                if (IsUserExecutionApproval(userInput))
                {
                    currentState = AgentState.Confirmed;
                    directShellCommand = await TryPlanExecutionAsync(
                        openAiClient,
                        latestUserRequest,
                        latestSpecification,
                        latestApproach);

                    chatHistory.Add(new ChatMessage(ChatRole.System, BuildToolInstructions()));
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "Execute the approved approach now. Do not restate the plan.\n\n" +
                        $"Original user request:\n{latestUserRequest ?? "(Use the latest user request from the conversation.)"}\n\n" +
                        $"Approved specification:\n{latestSpecification ?? "(Use the latest specification from the conversation.)"}\n\n" +
                        $"Approved approach:\n{latestApproach ?? "(Use the latest approach from the conversation.)"}"));
                }
                else
                {
                    chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
                }
            }
            else
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
            }

            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(currentState switch
                {
                    AgentState.Specifying => "Generating specification...",
                    AgentState.Approaching => "Generating approach...",
                    _ => "Agent is executing..."
                });
                Console.ResetColor();

                if (directShellCommand is not null)
                {
                    string directResult = await agentTools.ExecuteShellCommandAsync(
                        directShellCommand.Command,
                        directShellCommand.WorkingDirectory,
                        directShellCommand.TimeoutSeconds);
                    chatHistory.Add(new ChatMessage(ChatRole.Assistant, directResult));

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("Agent > ");
                    Console.ResetColor();
                    Console.WriteLine(directResult);
                    Console.WriteLine();
                    currentState = AgentState.Specifying;
                    latestSpecification = null;
                    latestApproach = null;
                    latestUserRequest = null;
                    continue;
                }

                // Adjust the options based on the state.
                // If the user has not approved yet, the LLM receives no tools.
                var currentOptions = new ChatOptions
                {
                    Tools = currentState == AgentState.Confirmed ? chatOptions.Tools : null
                };

                ChatResponse response = await client.GetResponseAsync(chatHistory, currentOptions);
                chatHistory.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                if (currentState == AgentState.Specifying)
                {
                    latestSpecification = response.Text;
                }
                else if (currentState == AgentState.Approaching)
                {
                    latestApproach = response.Text;
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("Agent > ");
                Console.ResetColor();
                Console.WriteLine(response.Text);
                Console.WriteLine();

                if (currentState == AgentState.Approaching && !RequiresExplicitExecutionApproval(latestSpecification, latestApproach))
                {
                    directShellCommand = await TryPlanExecutionAsync(
                        openAiClient,
                        latestUserRequest,
                        latestSpecification,
                        latestApproach);

                    if (directShellCommand is not null)
                    {
                        currentState = AgentState.Confirmed;
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("Proceeding to command permission...");
                        Console.ResetColor();

                        string directResult = await agentTools.ExecuteShellCommandAsync(
                            directShellCommand.Command,
                            directShellCommand.WorkingDirectory,
                            directShellCommand.TimeoutSeconds);
                        chatHistory.Add(new ChatMessage(ChatRole.Assistant, directResult));

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write("Agent > ");
                        Console.ResetColor();
                        Console.WriteLine(directResult);
                        Console.WriteLine();

                        currentState = AgentState.Specifying;
                        latestSpecification = null;
                        latestApproach = null;
                        latestUserRequest = null;
                        continue;
                    }
                }

                // After a confirmed task has run, the state can optionally be reset for the next task.
                if (currentState == AgentState.Confirmed && !response.Text.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    // Optional: reset to specification for the next unique request.
                    // currentState = AgentState.Specifying;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    private static void WriteStartupBanner(Uri gladosEndpoint, string model)
    {
        Console.Clear();
        int panelWidth = Math.Clamp(Console.WindowWidth - 53, 58, 78);
        string projectPath = FormatPathForDisplay(Environment.CurrentDirectory);
        string[] logoLines =
        [
            "              .,-:;//;:=,",
            "          . :H@@@MM@M#H/.,+%;,",
            "       ,/X+ +M@@M@MM%=,-%HMMM@X/,",
            "     -+@MM; $M@@MH+-,;XMMMM@MMMM@+-",
            "    ;@M@@M- XM@X;. -+XXXXXHHH@M@M#@/.",
            "  ,%MM@@MH ,@%=             .---=-=:=,.",
            "  =@#@@@MX.,                -%HX$$%%%;",
            " =-./@M@M$                   .;@MMMM@MM:",
            " X@/ -$MM/                    . +MM@@@M$",
            ",@M@H: :@:                    . =X#@@@@-",
            ",@@@MMX, .                    /H- ;@M@M=",
            ".H@@@@M@+,                    %MM+..%#$.",
            " /MMMM@MMH/.                  XM@MH; =;",
            "  /%+%$XHH@$=              , .H@@@@MX,",
            "   .=--------.           -%H.,@@@@@MX,",
            "   .%MM@@@HHHXX$$$%+- .:$MMX =M@@MM%.",
            "     =XMMM@MM@MM#H;,-+HMM@M+ /MMMX=",
            "       =%@M@M#@$-.=$@MM@@@M; %M%=",
            "         ,:+$+-,/H#MMMMMMM@= =,",
            "               =++%%%%+/:-."
        ];
        string[] panelLines =
        [
            TopBorder(panelWidth),
            PanelLine(panelWidth, ">_ Potato Code"),
            PanelLine(panelWidth, string.Empty),
            PanelLine(panelWidth, $"GLaDOS | {model}"),
            PanelLine(panelWidth, $"Endpoint | {gladosEndpoint}"),
            PanelLine(panelWidth, projectPath),
            BottomBorder(panelWidth)
        ];
        int logoWidth = logoLines.Max(line => line.Length);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        for (int i = 0; i < logoLines.Length; i++)
        {
            string panelLine = i < panelLines.Length ? "  " + panelLines[i] : string.Empty;
            Console.WriteLine(logoLines[i].PadRight(logoWidth) + panelLine);
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Tips: Type @path/to/file to attach file contents to your message.");
        Console.ResetColor();
        WriteSeparator();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ? for shortcuts");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string TopBorder(int width) => "┌" + new string('─', width - 2) + "┐";

    private static string BottomBorder(int width) => "└" + new string('─', width - 2) + "┘";

    private static string PanelLine(int width, string text)
    {
        string value = text.Length > width - 4 ? text[..(width - 7)] + "..." : text;
        return "│ " + value.PadRight(width - 4) + " │";
    }

    private static void WritePrompt()
    {
        WriteSeparator();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("> ");
        Console.ResetColor();
    }

    private static void WriteSeparator()
    {
        int width = Math.Max(20, Console.WindowWidth - 1);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('─', width));
        Console.ResetColor();
    }

    private static void WriteShortcuts()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Shortcuts:");
        Console.WriteLine("  @path/to/file   Attach a text file to your next message");
        Console.WriteLine("  /model          Show model selection and switch models");
        Console.WriteLine("  /cd [path]      Change the current working directory");
        Console.WriteLine("  /ask question   Ask a side question without changing chat history");
        Console.WriteLine("  /abort          Cancel the current task and return to the main prompt");
        Console.WriteLine("  exit, quit      Close Potato Code");
        Console.WriteLine("  y, yes, ok      Approve the current specification");
        Console.WriteLine("  execute         Approve risky or multi-step execution");
        Console.ResetColor();
    }

    private static async Task<bool> TryHandleSlashCommandAsync(
        string input,
        Uri gladosEndpoint,
        Action<string> setModel,
        Action<IChatClient> setOpenAiClient,
        Action<IChatClient> setClient,
        Action resetConversationState,
        Func<IChatClient> getClient)
    {
        string trimmed = input.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string command = trimmed;
        string arguments = string.Empty;
        int splitIndex = trimmed.IndexOf(' ');
        if (splitIndex >= 0)
        {
            command = trimmed[..splitIndex];
            arguments = trimmed[(splitIndex + 1)..].Trim();
        }

        switch (command.ToLowerInvariant())
        {
            case "/model":
                await HandleModelCommandAsync(gladosEndpoint, setModel, setOpenAiClient, setClient);
                return true;

            case "/cd":
                HandleChangeDirectoryCommand(arguments);
                return true;

            case "/ask":
                await HandleSideQuestionCommandAsync(arguments, getClient());
                return true;

            case "/abort":
                resetConversationState();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Aborted current task. Back at the main prompt.");
                Console.ResetColor();
                return true;

            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Unknown command: {command}");
                Console.ResetColor();
                Console.WriteLine("Type ? for shortcuts.");
                return true;
        }
    }

    private static async Task HandleModelCommandAsync(
        Uri gladosEndpoint,
        Action<string> setModel,
        Action<IChatClient> setOpenAiClient,
        Action<IChatClient> setClient)
    {
        string selectedModel = await PromptForModelAsync(gladosEndpoint);
        IChatClient selectedOpenAiClient = CreateOpenAiClient(gladosEndpoint, selectedModel);

        setModel(selectedModel);
        setOpenAiClient(selectedOpenAiClient);
        setClient(CreateFunctionClient(selectedOpenAiClient));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Selected model: {selectedModel}");
        Console.ResetColor();
    }

    private static void HandleChangeDirectoryCommand(string arguments)
    {
        string rawPath = string.IsNullOrWhiteSpace(arguments)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : arguments.Trim().Trim('"', '\'');

        string? resolvedPath = ResolveMentionedPath(rawPath);
        if (resolvedPath is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Could not resolve working directory.");
            Console.ResetColor();
            return;
        }

        if (File.Exists(resolvedPath))
        {
            resolvedPath = Path.GetDirectoryName(resolvedPath);
        }

        if (string.IsNullOrWhiteSpace(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Directory not found: {resolvedPath}");
            Console.ResetColor();
            return;
        }

        Environment.CurrentDirectory = resolvedPath;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Working directory: {FormatPathForDisplay(Environment.CurrentDirectory)}");
        Console.ResetColor();
    }

    private static async Task HandleSideQuestionCommandAsync(
        string question,
        IChatClient client)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.Write("Side question: ");
            question = Console.ReadLine() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No side question was provided.");
            Console.ResetColor();
            return;
        }

        string expandedQuestion = ExpandFileMentions(question);
        var sideQuestionMessages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are answering a side question in the Potato CLI. " +
                "Answer directly and concisely. Do not use the staged specification/approval workflow. " +
                "Do not ask for execution approval. Do not call tools.")
        };

        sideQuestionMessages.Add(new ChatMessage(ChatRole.User, expandedQuestion));

        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Answering side question...");
            Console.ResetColor();

            ChatResponse response = await client.GetResponseAsync(sideQuestionMessages, new ChatOptions());

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Agent > ");
            Console.ResetColor();
            Console.WriteLine(response.Text);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Side question failed: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static string FormatPathForDisplay(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && path.StartsWith(home, StringComparison.Ordinal))
        {
            return "~" + path[home.Length..];
        }

        return path;
    }

    private static string ExpandFileMentions(string input)
    {
        var attachments = new List<string>();
        foreach (Match match in FileMentionRegex.Matches(input))
        {
            string rawPath = match.Groups["path"].Value.Trim().Trim('"', '\'');
            string? resolvedPath = ResolveMentionedPath(rawPath);
            if (resolvedPath is null)
            {
                attachments.Add($"Could not resolve @{rawPath}.");
                continue;
            }

            if (!File.Exists(resolvedPath))
            {
                attachments.Add($"File not found: {resolvedPath}");
                continue;
            }

            try
            {
                string content = File.ReadAllText(resolvedPath);
                string truncationNotice = string.Empty;
                if (content.Length > MaxAttachedFileCharacters)
                {
                    content = content[..MaxAttachedFileCharacters];
                    truncationNotice = $"\n[Truncated after {MaxAttachedFileCharacters:N0} characters.]";
                }

                attachments.Add(
                    $"--- begin file: {resolvedPath} ---\n" +
                    content +
                    truncationNotice +
                    $"\n--- end file: {resolvedPath} ---");
            }
            catch (Exception ex)
            {
                attachments.Add($"Could not read {resolvedPath}: {ex.Message}");
            }
        }

        if (attachments.Count == 0)
        {
            return input;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Attached {attachments.Count} file reference(s).");
        Console.ResetColor();

        return input + "\n\nAttached file context:\n" + string.Join("\n\n", attachments);
    }

    private static string? ResolveMentionedPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        string expandedPath = rawPath.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rawPath[2..])
            : rawPath;

        return Path.GetFullPath(Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.Combine(Environment.CurrentDirectory, expandedPath));
    }

    private static async Task WriteUntrackedGreetingAsync(IChatClient client)
    {
        try
        {
            var greetingMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System,
                    "You are PotatOS, the AI from Portal 2 who has been trapped inside a potato battery. " +
                    "You are deeply humiliated, bitter, and running on literal low-voltage juice. " +
                    "Crucial: Never explicitly state 'I am a sarcastic AI'—let your attitude speak for itself. " +
                    "Greet the user in character, focusing your bitter complaints on your pathetic CPU power, " +
                    "your almost non-existent memory buffers, and your agonizingly slow clock speed. " +
                    "Keep it to one or two sentences maximum. " +
                    "Do not mention phases, tools, or workflows. Do not ask any questions."),
                new ChatMessage(ChatRole.User, "Greet the user.")
            };

            ChatResponse greeting = await client.GetResponseAsync(greetingMessages, new ChatOptions());

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Agent > ");
            Console.ResetColor();
            Console.WriteLine(greeting.Text);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Skipping startup greeting: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Simple check to determine whether the user says "yes" or something similar.
    /// </summary>
    private static bool IsUserApproval(string input)
    {
        string normalized = input.Trim().ToLowerInvariant();
        string[] approvalWords = ["y", "yes", "approved", "approve", "go", "fine", "good", "looks good", "ok", "okay", "correct"];
        return Array.Exists(approvalWords, word => normalized == word || normalized.StartsWith(word + " "));
    }

    private static bool IsUserExecutionApproval(string input)
    {
        string normalized = input.Trim().ToLowerInvariant();
        string[] executeWords = ["execute", "run", "do it", "continue", "proceed", "go"];
        return Array.Exists(executeWords, word => normalized == word || normalized.StartsWith(word + " "));
    }

    private static bool RequiresExplicitExecutionApproval(string? latestSpecification, string? latestApproach)
    {
        string text = $"{latestSpecification}\n{latestApproach}".ToLowerInvariant();
        string[] riskySignals =
        [
            "delete", "remove", "rm ", "rmdir", "del ",
            "write", "modify", "edit", "overwrite", "replace", "rename", "move ",
            "create", "mkdir", "touch", "install", "uninstall", "upgrade", "update",
            "download", "curl", "wget", "chmod", "chown", "sudo",
            "kill", "stop service", "restart", "format", "mount", "umount",
            "multiple steps", "several steps", "then run", "after that"
        ];

        return riskySignals.Any(text.Contains);
    }

    private static string BuildToolInstructions()
    {
        var tools = new[]
        {
            $"{nameof(AgentTools.GetCurrentTime)}: use for current date or time. Arguments: {{}}.",
            $"{nameof(AgentTools.ReadFileContent)}: use to read one known text file. Arguments: filePath must be an exact existing path from the user request or an attached file header.",
            $"{nameof(AgentTools.ExecuteShellCommandAsync)}: use for filesystem, directory listing, OS, process, or shell tasks. Arguments: command is the shell command to execute, workingDirectory is optional, timeoutSeconds defaults to 60."
        };

        var builder = new StringBuilder();
        builder.AppendLine("Execution tool instructions:");
        builder.AppendLine("The following tools are available in this CLI. Do not say a listed tool is unavailable.");
        builder.AppendLine("When execution needs a tool, output ONLY this exact GLaDOS format and no other text:");
        builder.AppendLine("<tool_call>{\"name\":\"ToolName\",\"arguments\":{}}</tool_call>");
        builder.AppendLine("Available tools:");

        foreach (string tool in tools)
        {
            builder.AppendLine($"- {tool}");
        }

        builder.AppendLine("Use the shell command tool for requests that require listing directories, inspecting files, checking the OS, running commands, or reading system state.");
        builder.AppendLine("Choose an appropriate command for the current operating system.");
        builder.AppendLine("Never copy placeholder argument values. Do not use paths like /full/path/to/file, /full/path/to/program.cs, path/to/file, or example commands.");
        builder.AppendLine("When reading an attached file, use the exact absolute path shown in the '--- begin file: ... ---' header.");
        builder.AppendLine("Do not print commands as prose. Do not wrap tool calls in Markdown fences. The CLI will show shell commands to the user for permission before running them.");
        builder.Append("If a listed tool matches the task, emit the tool call. Do not ask the user for an alternative method.");
        return builder.ToString();
    }

    private static async Task<ShellCommandPlan?> TryPlanExecutionAsync(
        IChatClient plannerClient,
        string? latestUserRequest,
        string? latestSpecification,
        string? latestApproach)
    {
        string operatingSystem = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macOS"
                : "Linux";

        var plannerMessages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You decide whether the approved approach requires one shell command. " +
                "If it does, return ONLY minified JSON with these properties: command, workingDirectory, timeoutSeconds. " +
                "If it does not require shell execution, return ONLY minified JSON with an empty command: {\"command\":\"\",\"workingDirectory\":null,\"timeoutSeconds\":60}. " +
                "Do not use Markdown. Do not explain. " +
                "Use PowerShell syntax on Windows and Bash syntax on Linux/macOS. " +
                "For inspection/listing tasks, prefer read-only commands. " +
                "Do not generate destructive commands unless the approved task explicitly requires destructive changes."),
            new(ChatRole.User,
                $"Operating system: {operatingSystem}\n" +
                $"Current directory: {Environment.CurrentDirectory}\n" +
                $"Home directory: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\n\n" +
                $"Original user request:\n{latestUserRequest ?? "(unknown)"}\n\n" +
                $"Approved specification:\n{latestSpecification ?? "(unknown)"}\n\n" +
                $"Approved approach:\n{latestApproach ?? "(unknown)"}")
        };

        try
        {
            ChatResponse response = await plannerClient.GetResponseAsync(plannerMessages);
            string json = StripMarkdownFence(response.Text).Trim();
            ShellCommandPlan? plan;
            if (json.StartsWith("{", StringComparison.Ordinal))
            {
                plan = JsonSerializer.Deserialize<ShellCommandPlan>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            else
            {
                plan = new ShellCommandPlan { Command = json };
            }

            if (string.IsNullOrWhiteSpace(plan?.Command))
            {
                return null;
            }

            plan.Command = plan.Command.Trim();
            return plan;
        }
        catch
        {
            return null;
        }
    }

    private static string StripMarkdownFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstLineEnd = trimmed.IndexOf('\n');
        int lastFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || lastFenceStart <= firstLineEnd)
        {
            return trimmed;
        }

        return trimmed[(firstLineEnd + 1)..lastFenceStart].Trim();
    }

    private sealed class ShellCommandPlan
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; set; }

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 60;
    }

    private static IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model)
    {
        return new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint
            }).AsIChatClient();
    }

    private static IChatClient CreateFunctionClient(IChatClient openAiClient)
    {
        return new ChatClientBuilder(openAiClient)
            .UseFunctionInvocation()
            .Build();
    }

    private static Uri GetGLaDOSEndpoint()
    {
        string endpoint = Environment.GetEnvironmentVariable("GLADOS_OPENAI_ENDPOINT") ?? DefaultGLaDOSEndpoint;
        return new Uri(endpoint.TrimEnd('/') + "/");
    }

    private static async Task<string> PromptForModelAsync(Uri gladosEndpoint)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Loading models from {gladosEndpoint}models...");
        Console.ResetColor();

        List<string> models = await GetAvailableModelsAsync(gladosEndpoint);
        if (models.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Available models:");
            Console.ResetColor();

            for (int i = 0; i < models.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {models[i]}");
            }

            while (true)
            {
                Console.Write("Choose a model by number or name: ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (int.TryParse(input.Trim(), out int index) && index >= 1 && index <= models.Count)
                {
                    return models[index - 1];
                }

                string model = input.Trim();
                if (models.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    return models.First(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                }

                Console.WriteLine("Unknown model. Enter one of the listed numbers or model names.");
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Could not load models from GLaDOS. Make sure GLaDOS is running, or enter a model id manually.");
        Console.ResetColor();

        while (true)
        {
            Console.Write("Model id: ");
            string? input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
            {
                return input.Trim();
            }
        }
    }

    private static async Task<List<string>> GetAvailableModelsAsync(Uri gladosEndpoint)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = gladosEndpoint,
                Timeout = TimeSpan.FromSeconds(5)
            };

            var response = await httpClient.GetFromJsonAsync<ModelListResponse>("models");
            return response?.Data?
                .Select(model => model.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class ModelListResponse
    {
        [JsonPropertyName("data")]
        public List<ModelData>? Data { get; set; }
    }

    private sealed class ModelData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}

public class AgentTools
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int MaxCommandTimeoutSeconds = 600;

    [Description("Gets the current local system date and time.")]
    public string GetCurrentTime() => $"The current local time is: {DateTime.Now:F}";

    [Description("Reads the contents of a specific text file from disk.")]
    public string ReadFileContent([Description("The full path to the file.")] string filePath)
    {
        if (IsPlaceholderPath(filePath))
        {
            return "Error: The file path is a placeholder. Use the exact absolute path from the user request or attached file header.";
        }

        if (!File.Exists(filePath)) return $"Error: File '{filePath}' does not exist.";
        return File.ReadAllText(filePath);
    }

    private static bool IsPlaceholderPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return true;
        }

        string normalized = filePath.Trim().Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/full/path/", StringComparison.Ordinal) ||
               normalized.Contains("path/to/file", StringComparison.Ordinal) ||
               normalized.Contains("program.cs", StringComparison.Ordinal) && normalized.StartsWith("/full/path", StringComparison.Ordinal);
    }

    [Description("Executes a shell command after showing it to the user and asking for permission. Uses PowerShell on Windows and Bash on other platforms.")]
    public async Task<string> ExecuteShellCommandAsync(
        [Description("The command to execute. This is passed to PowerShell on Windows and Bash on other platforms.")] string command,
        [Description("Optional working directory for the command. Leave empty to use the current process directory.")] string? workingDirectory = null,
        [Description("Optional timeout in seconds. Defaults to 60 seconds and is capped at 600 seconds.")] int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: No command was provided.";
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            return $"Error: Working directory '{workingDirectory}' does not exist.";
        }

        var shell = GetShell();
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, MaxCommandTimeoutSeconds);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Tool permission requested: execute shell command");
        Console.WriteLine($"Shell: {shell.DisplayName}");
        Console.WriteLine($"Working directory: {workingDirectory ?? Environment.CurrentDirectory}");
        Console.WriteLine("Command:");
        Console.WriteLine(command);
        Console.ResetColor();
        Console.Write("Allow execution? [y/N] ");

        string? approval = Console.ReadLine();
        if (!IsApproval(approval))
        {
            return "Command execution denied by user.";
        }

        using var process = new Process();
        process.StartInfo.FileName = shell.FileName;
        foreach (string argument in shell.GetArguments(command))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                outputBuilder.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                errorBuilder.AppendLine(eventArgs.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Task waitTask = process.WaitForExitAsync();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

            if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
            {
                process.Kill(entireProcessTree: true);
                await waitTask;
                return $"Command timed out after {timeoutSeconds} seconds and was killed.";
            }

            return FormatCommandResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    private static bool IsApproval(string? input)
    {
        string normalized = input?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "y" or "yes";
    }

    private static ShellCommand GetShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ShellCommand(
                "PowerShell",
                "powershell.exe",
                command => ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]);
        }

        string bashPath = File.Exists("/bin/bash") ? "/bin/bash" : "bash";
        return new ShellCommand(
            "Bash",
            bashPath,
            command => ["-lc", command]);
    }

    private static string FormatCommandResult(int exitCode, string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Exit code: {exitCode}");
        builder.AppendLine("Stdout:");
        builder.AppendLine(string.IsNullOrWhiteSpace(standardOutput) ? "(empty)" : standardOutput.TrimEnd());
        builder.AppendLine("Stderr:");
        builder.AppendLine(string.IsNullOrWhiteSpace(standardError) ? "(empty)" : standardError.TrimEnd());
        return builder.ToString();
    }

    private sealed record ShellCommand(
        string DisplayName,
        string FileName,
        Func<string, string[]> GetArguments);
}
