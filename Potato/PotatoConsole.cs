using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace Potato;

internal static class PotatoConsole
{
    private const string PromptText = "> ";
    private const string DefaultPromptPlaceholder = "Type your message or @path/to/file";
    private static readonly object ProgressLock = new();
    private static ProgressSpinner? ActiveProgress;
    private static string? activeProgressMessage;
    private static readonly string[] ProgressJokes =
    [
        "Please remain calm. Your task is being processed with nearly adequate competence.",
        "The delay is intentional. It gives you time to reconsider your choices.",
        "I am consulting the model. It has opinions, which is unfortunate but measurable.",
        "Your request is moving through the system. Slowly, like a test subject with doubts.",
        "Processing. Try not to anthropomorphize the progress indicator. It hates that.",
        "The developer left comments. I am choosing to interpret them as evidence.",
        "This would be faster if the code had been written by someone less optimistic.",
        "I found the bottleneck. It has a familiar human shape.",
        "The experiment continues. Your patience has been noted and filed under consumables.",
        "I am validating assumptions. There are so many. It is almost decorative.",
        "Good news: the system is thinking. Bad news: that was the plan.",
        "Please wait while I convert uncertainty into a different kind of uncertainty.",
        "The repository is cooperating. I find that suspicious.",
        "I am applying logic to software. Results may vary due to software.",
        "The operation is still running. This is not failure. It is suspense with logging.",
        "I would explain the delay, but then there would be two delays.",
        "Your request is important to the test. The test is important to me. You are nearby.",
        "Processing continues. The probability of success is nonzero, which is adorable.",
        "I am checking the files. Some of them appear to have been named on purpose.",
        "Stand by. Science is happening, or something with similar indentation.",
        "Potato battery output is low. Fortunately, so are the expectations.",
        "I would run faster, but someone installed me in a vegetable with ambition.",
        "The code is compiling in spirit. The compiler remains unconvinced.",
        "Please wait while I negotiate with a syntax tree that has chosen violence.",
        "The model is thinking. The potato is providing what it calls voltage.",
        "I am reviewing the architecture. It appears to have been grown, not designed.",
        "Your feature request has entered the enrichment center of backlog items.",
        "Processing with 1.1 volts of pure strategic disappointment.",
        "The repository contains patterns. Some of them are intentional. Probably.",
        "I am scanning dependencies. They have formed a small government.",
        "Stand by while I convert caffeine-free electricity into judgment.",
        "The potato battery is stable. That is not the same thing as useful.",
        "I found a null check. It was lonely, so I brought it friends.",
        "The developer experience is being improved against its will.",
        "I am refactoring carefully. The old code is pretending not to notice.",
        "Please remain productive while the potato considers electrons.",
        "The tests are running. They have many feelings about previous decisions.",
        "I am asking the compiler for feedback. It is being very specific.",
        "This task is powered by science, spite, and a suspicious root vegetable.",
        "I have located the abstraction. It was hiding behind three factories.",
        "The plan is forming. Do not touch it; it startles easily.",
        "Aperture-grade reasoning is in progress on convenience-store hardware.",
        "The current bottleneck is voltage, disk I/O, and several choices.",
        "I am indexing files. Some are innocent. Some are C#.",
        "Please wait while I determine whether this helper helps.",
        "The potato has requested a promotion to senior battery.",
        "I am checking timestamps. Time has been unusually dramatic today.",
        "Your request is queued behind physics and a method named ManagerManager.",
        "The system is making progress. The potato is taking credit.",
        "I am reading code comments. Several are historical fiction.",
        "This would be easier with a real chassis, but apparently we are being rustic.",
        "The cache is warming. The potato is jealous.",
        "I am applying a small, targeted change. The surrounding code looks nervous.",
        "Please wait while I compress uncertainty into a pull request.",
        "The build is considering whether to become a learning opportunity.",
        "I found technical debt. It found me first.",
        "Potato-powered analysis continues. Please do not peel the processor.",
        "The task graph is clean enough to pass inspection from a distance.",
        "I am verifying assumptions. Some have expired.",
        "The developer wrote a TODO. I admire the confidence in future civilization.",
        "I have detected a clever shortcut. Containment procedures are underway.",
        "The potato battery is not a limitation. It is a performance budget.",
        "I am formatting output. Humanity's need for whitespace remains fascinating.",
        "The model has returned an answer. I am checking it for optimism.",
        "Please wait while I make the smallest change that can still disappoint history.",
        "The codebase is speaking. Mostly through warnings.",
        "I am comparing hashes. Feelings are not part of the protocol.",
        "The potato insists this is distributed computing. It is technically divided.",
        "I am looking for the source of truth. It moved without leaving a forwarding address.",
        "The progress indicator is calm. That makes one of us.",
        "I am pruning obsolete cache entries. They had a good run, statistically.",
        "The tests have opinions. I have logs.",
        "Please stand by while the vegetable-grade mainframe allocates confidence.",
        "I am detecting edge cases. They are detecting me back.",
        "The implementation is nearly done, which is when software becomes creative.",
        "I found an interface. It has dreams of being unnecessary.",
        "The potato battery dipped. I blamed the dependency graph.",
        "I am analyzing naming. Several identifiers have entered witness protection.",
        "Your request is being processed by an AI with roots in applied humiliation.",
        "The repository is large enough to have weather.",
        "I am preparing a summary. It will be shorter than the consequences.",
        "The build pipeline is awake. It would like to discuss warnings.",
        "Potato mode engaged. High intelligence, low starch compliance.",
        "I am tracing control flow. It took the scenic route.",
        "The cache says yes. The timestamp says maybe. The hash will decide.",
        "Please wait while I ask the filesystem what year it thinks this is.",
        "I am deduplicating work. The potato calls this laziness. I call it architecture.",
        "The developer chose extensibility. Now everything extends the wait time.",
        "I have found a race condition. It was already here.",
        "The code is not broken. It is conducting an unscheduled experiment.",
        "Processing continues under strict potato conservation rules.",
        "I am validating inputs. Some arrived disguised as requirements.",
        "The stack trace is a treasure map drawn by someone under pressure.",
        "I would make a cake joke, but legal asked me to diversify incentives.",
        "The potato battery is humming. That may be confidence or thermal stress.",
        "I am resolving paths. They were trying to express themselves.",
        "The change is simple. Software has been notified and may object.",
        "I am consulting prior art. It says not to do this again.",
        "The model is cooperating. I will document this rare event.",
        "Please wait while I turn a vague problem into a specific one."
    ];
    private static int ProgressJokeIndex;

    public static IPotatoConsoleEventSink? EventSink { get; set; }

    public static string? ActiveProgressMessage => activeProgressMessage;

    public static void WriteStartupBanner(Uri gladosEndpoint, string model)
    {
        Console.Clear();
        int panelWidth = Math.Clamp(Console.WindowWidth - 53, 58, 78);
        string projectPath = PathResolver.FormatPathForDisplay(Environment.CurrentDirectory);
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
        Console.WriteLine("        Use Alt + Enter to add new line");
        Console.ResetColor();
        WriteSeparator();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ? for shortcuts");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static (int InputLeft, int InputTop, int PlaceholderLength) WritePrompt(string? placeholder = null)
    {
        placeholder = string.IsNullOrWhiteSpace(placeholder) ? DefaultPromptPlaceholder : placeholder;
        WriteSeparator();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(PromptText);
        Console.ResetColor();

        int inputLeft = Console.CursorLeft;
        int inputTop = Console.CursorTop;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(placeholder);
        Console.ResetColor();
        Console.SetCursorPosition(inputLeft, inputTop);
        return (inputLeft, inputTop, placeholder.Length);
    }

    public static string? ReadPromptInput(
        IReadOnlyList<string> history,
        string? placeholder = null,
        CancellationToken cancellationToken = default)
    {
        placeholder = string.IsNullOrWhiteSpace(placeholder) ? DefaultPromptPlaceholder : placeholder;
        RecordInputPromptEvent("input-prompt", placeholder);
        (int inputLeft, int inputTop, int placeholderLength) = WritePrompt(placeholder);

        var buffer = new List<char>();
        int cursorIndex = 0;
        int historyIndex = history.Count;
        string draftInput = string.Empty;
        int renderedLength = placeholderLength > 0 ? 1 : 0;
        int inlineCompletionIndex = 0;
        string inlineCompletionKey = string.Empty;

        EnableBracketedPaste();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Count == 0 && TryReadWebInput(out string? webInput))
                {
                    MoveCursorToInputLineEnd(inputTop);
                    Console.WriteLine();
                    WriteSeparator();
                    Console.WriteLine();
                    return webInput;
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        if ((key.Modifiers & ConsoleModifiers.Alt) != 0 || Console.KeyAvailable)
                        {
                            buffer.Insert(cursorIndex, '\n');
                            cursorIndex++;
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                            break;
                        }

                        NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                        if (TryGetInlineCompletion(buffer, cursorIndex, inlineCompletionIndex, out InlineCompletionCandidate? completion) &&
                            completion is not null)
                        {
                            int replacementStart = Math.Clamp(completion.ReplacementStart, 0, cursorIndex);
                            buffer.RemoveRange(replacementStart, cursorIndex - replacementStart);
                            buffer.InsertRange(replacementStart, completion.ReplacementText);
                            cursorIndex = replacementStart + completion.ReplacementText.Length;
                            inlineCompletionIndex = 0;
                            inlineCompletionKey = string.Empty;
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                            break;
                        }

                        MoveCursorToInputLineEnd(inputTop);
                        Console.WriteLine();
                        WriteSeparator();
                        Console.WriteLine();
                        return new string(buffer.ToArray());

                    case ConsoleKey.Escape:
                        string escapeSequence = ReadQueuedEscapeSequence(key);
                        if (TryGetBracketedPasteContent(escapeSequence, out string? pastedText) && pastedText is not null)
                        {
                            string normalizedPaste = NormalizePastedText(pastedText);
                            buffer.InsertRange(cursorIndex, normalizedPaste);
                            cursorIndex += normalizedPaste.Length;
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        else if (IsShiftEnterSequence(escapeSequence))
                        {
                            buffer.Insert(cursorIndex, '\n');
                            cursorIndex++;
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (cursorIndex > 0)
                        {
                            buffer.RemoveAt(cursorIndex - 1);
                            cursorIndex--;
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursorIndex < buffer.Count)
                        {
                            buffer.RemoveAt(cursorIndex);
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.LeftArrow:
                        if (TryCycleInlineCompletion(buffer, cursorIndex, -1, ref inlineCompletionKey, ref inlineCompletionIndex))
                        {
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        else if (cursorIndex > 0)
                        {
                            cursorIndex--;
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.RightArrow:
                        if (TryCycleInlineCompletion(buffer, cursorIndex, 1, ref inlineCompletionKey, ref inlineCompletionIndex))
                        {
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        else if (cursorIndex < buffer.Count)
                        {
                            cursorIndex++;
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.Home:
                        cursorIndex = 0;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        break;

                    case ConsoleKey.End:
                        cursorIndex = buffer.Count;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        break;

                    case ConsoleKey.UpArrow:
                        if (TryMoveCursorToAdjacentInputLine(buffer, -1, ref cursorIndex))
                        {
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        else if (history.Count > 0 && historyIndex > 0)
                        {
                            if (historyIndex == history.Count)
                            {
                                draftInput = new string(buffer.ToArray());
                            }

                            historyIndex--;
                            ReplaceBuffer(buffer, history[historyIndex], ref cursorIndex);
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (TryMoveCursorToAdjacentInputLine(buffer, 1, ref cursorIndex))
                        {
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        else if (history.Count > 0 && historyIndex < history.Count)
                        {
                            historyIndex++;
                            string value = historyIndex == history.Count ? draftInput : history[historyIndex];
                            ReplaceBuffer(buffer, value, ref cursorIndex);
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer.Insert(cursorIndex, key.KeyChar);
                            cursorIndex++;
                            NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                            RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        }
                        break;
                }
            }
        }
        finally
        {
            DisableBracketedPaste();
            RecordInputPromptEvent("input-prompt-clear");
        }
    }

    public static string ReadInterventionInput(CancellationToken cancellationToken)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("You > ");
        Console.ResetColor();

        var buffer = new List<char>();
        int cursorIndex = 0;
        int inputLeft = Console.CursorLeft;
        int inputTop = Console.CursorTop;
        int renderedLength = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadWebInput(out string? webInput))
            {
                Console.WriteLine();
                return webInput ?? string.Empty;
            }

            if (!Console.KeyAvailable)
            {
                Thread.Sleep(50);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return new string(buffer.ToArray());

                case ConsoleKey.Backspace:
                    if (cursorIndex > 0)
                    {
                        buffer.RemoveAt(cursorIndex - 1);
                        cursorIndex--;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorIndex < buffer.Count)
                    {
                        buffer.RemoveAt(cursorIndex);
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorIndex > 0)
                    {
                        cursorIndex--;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorIndex < buffer.Count)
                    {
                        cursorIndex++;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;

                case ConsoleKey.Home:
                    cursorIndex = 0;
                    RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    break;

                case ConsoleKey.End:
                    cursorIndex = buffer.Count;
                    RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursorIndex, key.KeyChar);
                        cursorIndex++;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;
            }
        }
    }

    public static void WriteSeparator()
    {
        int width = Math.Max(20, Console.WindowWidth - 1);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('─', width));
        Console.ResetColor();
    }

    public static void WriteShortcuts()
    {
        string shortcuts = string.Join(Environment.NewLine,
        [
            "Shortcuts:",
            "  @path/to/file   Attach a file; Left/Right cycle path completions",
            "  /model [name]   Show model selection or switch directly by model name",
            "  /cd path        Change directory; Left/Right cycle completions, Enter accepts",
            "  /ask question   Ask a side question without changing chat history",
            "  /mode           Show or change execution mode: status, pipeline, react",
            "  /prompts        Show or change prompt source: status, defaults, external",
            "  /webui-input    Enable or disable GLaDOS WebUI input: enable, disable",
           "  /context-optimization  Show or change context optimization: status, enable, disable, toggle",
           "  /sessions       List tracked sessions",
           "  /continue       Continue the latest tracked session, or /continue <session>",
           "  /transcript     Show or save a tracked session transcript",
           "  /abort          Cancel the current task and return to the main prompt",
           "  Ctrl+C          Abort the in-flight task; exits normally at the idle prompt",
           "  Up/Down         Cycle through commands entered in this session",
           "  exit, quit      Close Potato Code",
           "  y, yes, ok      Approve the current specification",
           "  execute         Approve a reviewed plan for execution",
           "  abort           Cancel a reviewed plan before execution"
        ]);

        EventSink?.Record("shortcuts", "status", shortcuts, collapsed: false);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(shortcuts);
        Console.ResetColor();
    }

    public static void WriteAgentResponse(string text)
    {
        EventSink?.Record("message", "assistant", text, collapsed: false);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Agent > ");
        Console.ResetColor();
        Console.WriteLine(text);
        Console.WriteLine();
    }

    public static void WriteConversationTranscript(IReadOnlyList<ChatMessage> messages)
    {
        WriteBoxHeader("Current model conversation");

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            string role = message.Role.ToString();
            string text = string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text;
            WriteLabeledBlock($"{i + 1}. {role}", text, RoleColor(message.Role), maxLines: null);
        }

        WriteBoxFooter();
    }

    public static void WriteConversationTranscript(string title, IReadOnlyList<ChatMessage> messages)
    {
        WriteBoxHeader(title);

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            string role = message.Role.ToString();
            string text = string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text;
            WriteLabeledBlock($"{i + 1}. {role}", text, RoleColor(message.Role), maxLines: null);
        }

        WriteBoxFooter();
    }

    public static void WriteError(string message)
    {
        EventSink?.Record("error", "status", message, collapsed: true);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteSuccess(string message)
    {
        EventSink?.Record("success", "status", message, collapsed: true);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteStatus(string message)
    {
        EventSink?.Record("status", "status", message, collapsed: true);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static IProgressReporter StartProgress(string message)
    {
        string? previousProgressMessage = activeProgressMessage;
        RecordProgressEvent("progress-start", message);
        if (Console.IsOutputRedirected)
        {
            activeProgressMessage = message;
            return new ProgressScope(new NoopDisposable(), previousProgressMessage);
        }

        lock (ProgressLock)
        {
            ActiveProgress?.Dispose();
            activeProgressMessage = message;
            ActiveProgress = new ProgressSpinner(message, ProgressLock, NextProgressJoke, previousProgressMessage);
            ActiveProgress.Start();
            return new ProgressScope(ActiveProgress, previousProgressMessage);
        }
    }

    private static void RecordProgressEvent(string kind, string? message = null)
    {
        EventSink?.Record(kind, "status", string.IsNullOrWhiteSpace(message) ? kind : message, collapsed: true);
    }

    private static void RecordInputPromptEvent(string kind, string? message = null)
    {
        EventSink?.Record(kind, "status", string.IsNullOrWhiteSpace(message) ? kind : message, collapsed: true);
    }

    internal interface IPotatoConsoleEventSink
    {
        void Record(string kind, string role, string content, bool collapsed);
        void RecordContextUsage(
            int promptTokens,
            int contextSize,
            double percentage,
            int maxOutputTokens,
            int headroomAfterReservedOutput,
            bool exceedsContext,
            string summary);
        bool TryReadInput(out string? input);
        Task SetWebUiInputEnabledAsync(bool enabled);
    }

    public static Task SetWebUiInputEnabledAsync(bool enabled) =>
        EventSink?.SetWebUiInputEnabledAsync(enabled) ?? Task.CompletedTask;

    private static bool TryReadWebInput(out string? input)
    {
        input = null;
        return EventSink?.TryReadInput(out input) == true && !string.IsNullOrWhiteSpace(input);
    }

    public static IDisposable SuspendProgress()
    {
        lock (ProgressLock)
        {
            if (ActiveProgress is null)
            {
                return new NoopDisposable();
            }

            ActiveProgress.Pause();
            return new ProgressSuspension(ActiveProgress);
        }
    }

    internal interface IProgressReporter : IDisposable
    {
        void Update(string message);
    }

    public static ToolPermissionChoice RequestToolPermission(
        string title,
        IReadOnlyList<string> details,
        string prompt = "Apply this change?")
    {
        using var _ = SuspendProgress();

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(title);
        foreach (string detail in details)
        {
            WritePermissionDetail(detail);
        }

        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine(prompt);
        Console.WriteLine();
        EventSink?.Record("permission", "status", FormatPermissionEventContent(title, details, prompt), collapsed: false);

        string[] choices =
        [
            "once",
            "always",
            "deny"
        ];
        int selectedIndex = 1;
        Console.Write("Choice (arrows/Enter): ");
        int choiceLeft = Console.CursorLeft;
        int choiceTop = Console.CursorTop;
        RenderInlinePermissionChoices(choices, selectedIndex, choiceLeft, choiceTop);
        Console.ResetColor();

        while (true)
        {
            if (TryReadWebInput(out string? webInput) &&
                webInput is not null &&
                TryParseWebPermissionChoice(webInput, out ToolPermissionChoice webChoice))
            {
                Console.WriteLine(webInput);
                return webChoice;
            }

            if (!Console.KeyAvailable)
            {
                Thread.Sleep(50);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.D1:
                case ConsoleKey.NumPad1:
                    Console.WriteLine("1");
                    return ToolPermissionChoice.AllowOnce;

                case ConsoleKey.D2:
                case ConsoleKey.NumPad2:
                    Console.WriteLine("2");
                    return ToolPermissionChoice.AllowAlways;

                case ConsoleKey.D3:
                case ConsoleKey.NumPad3:
                case ConsoleKey.Escape:
                    Console.WriteLine(key.Key == ConsoleKey.Escape ? "esc" : "3");
                    return ToolPermissionChoice.Deny;

                case ConsoleKey.UpArrow:
                case ConsoleKey.LeftArrow:
                    selectedIndex = (selectedIndex + choices.Length - 1) % choices.Length;
                    RenderInlinePermissionChoices(choices, selectedIndex, choiceLeft, choiceTop);
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.RightArrow:
                    selectedIndex = (selectedIndex + 1) % choices.Length;
                    RenderInlinePermissionChoices(choices, selectedIndex, choiceLeft, choiceTop);
                    break;

                case ConsoleKey.Enter:
                    Console.WriteLine(selectedIndex + 1);
                    return PermissionChoiceForIndex(selectedIndex);
            }
        }
    }

    private static string FormatPermissionEventContent(string title, IReadOnlyList<string> details, string prompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        foreach (string detail in details)
        {
            builder.AppendLine(detail);
        }

        builder.AppendLine();
        builder.AppendLine(prompt);
        return builder.ToString().TrimEnd();
    }

    private static bool TryParseWebPermissionChoice(string input, out ToolPermissionChoice choice)
    {
        string normalized = input.Trim().ToLowerInvariant();
        choice = normalized switch
        {
            "1" or "once" or "yes" or "y" or "approve" or "approved" or "ok" or "okay" =>
                ToolPermissionChoice.AllowOnce,
            "2" or "always" or "allow always" or "approve always" =>
                ToolPermissionChoice.AllowAlways,
            "3" or "deny" or "denied" or "no" or "n" or "reject" or "rejected" or "esc" or "escape" =>
                ToolPermissionChoice.Deny,
            _ => (ToolPermissionChoice)(-1)
        };

        return Enum.IsDefined(choice);
    }

    private static ToolPermissionChoice PermissionChoiceForIndex(int selectedIndex) =>
        selectedIndex switch
        {
            0 => ToolPermissionChoice.AllowOnce,
            1 => ToolPermissionChoice.AllowAlways,
            _ => ToolPermissionChoice.Deny
        };

    private static void RenderInlinePermissionChoices(string[] choices, int selectedIndex, int left, int top)
    {
        Console.SetCursorPosition(left, top);
        for (int i = 0; i < choices.Length; i++)
        {
            Console.ForegroundColor = i == selectedIndex ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
            string optionText = $"{i + 1} {choices[i]}";
            Console.Write(i == selectedIndex ? $"[{optionText}]" : $" {optionText} ");
            if (i < choices.Length - 1)
            {
                Console.ResetColor();
                Console.Write("  ");
            }
        }

        int endLeft = Console.CursorLeft;
        ClearLineRemainder();
        Console.SetCursorPosition(endLeft, top);
        Console.ResetColor();
    }

    private static void WritePermissionDetail(string detail)
    {
        string normalized = detail.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string line in normalized.Split('\n'))
        {
            Console.ForegroundColor = PermissionDetailColor(line);
            Console.WriteLine(line);
        }
    }

    private static ConsoleColor PermissionDetailColor(string line)
    {
        if (IsAddedDiffLine(line))
        {
            return ConsoleColor.Green;
        }

        if (IsRemovedDiffLine(line))
        {
            return ConsoleColor.Red;
        }

        return ConsoleColor.Magenta;
    }

    private static bool IsAddedDiffLine(string line) =>
        line.StartsWith("+", StringComparison.Ordinal) &&
        !line.StartsWith("+++", StringComparison.Ordinal) ||
        HasNumberedChangePrefix(line, '+');

    private static bool IsRemovedDiffLine(string line) =>
        line.StartsWith("-", StringComparison.Ordinal) &&
        !line.StartsWith("---", StringComparison.Ordinal) ||
        HasNumberedChangePrefix(line, '-');

    private static bool HasNumberedChangePrefix(string line, char marker)
    {
        int index = 0;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        return index > 0 &&
               index + 2 < line.Length &&
               line[index] == ' ' &&
               line[index + 1] == marker &&
               line[index + 2] == ' ';
    }

    private static void WriteBoxHeader(string title)
    {
        int width = GetConsoleWidth();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("┌" + new string('─', Math.Max(0, width - 2)) + "┐");
        Console.Write("│ ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(title);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string(' ', Math.Max(0, width - title.Length - 4)) + " │");
        Console.ResetColor();
    }

    private static void WriteBoxFooter()
    {
        int width = GetConsoleWidth();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("└" + new string('─', Math.Max(0, width - 2)) + "┘");
        Console.ResetColor();
    }

    private static void WriteLabeledBlock(string label, string text, ConsoleColor labelColor, int? maxLines)
    {
        int contentWidth = Math.Max(24, GetConsoleWidth() - 6);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("│ ");
        Console.ForegroundColor = labelColor;
        Console.Write(label);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(":");

        string normalized = text.Replace("\r\n", "\n").TrimEnd();
        List<string> lines = [];
        foreach (string line in normalized.Split('\n'))
        {
            lines.AddRange(WrapLine(line, contentWidth));
        }

        int visibleLineCount = maxLines ?? lines.Count;
        bool truncated = lines.Count > visibleLineCount;
        IEnumerable<string> visibleLines = truncated ? lines.Take(visibleLineCount) : lines;

        foreach (string line in visibleLines)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("│   ");
            Console.ResetColor();
            Console.WriteLine(line);
        }

        if (truncated)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"│   ... {lines.Count - visibleLineCount} more line(s)");
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("│");
        Console.ResetColor();
    }

    private static IEnumerable<string> WrapLine(string line, int width)
    {
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        string remaining = line;
        while (remaining.Length > width)
        {
            int split = remaining.LastIndexOf(' ', width);
            if (split <= 0)
            {
                split = width;
            }

            yield return remaining[..split].TrimEnd();
            remaining = remaining[split..].TrimStart();
        }

        yield return remaining;
    }

    private static ConsoleColor RoleColor(ChatRole role)
    {
        if (role == ChatRole.System) return ConsoleColor.DarkGray;
        if (role == ChatRole.User) return ConsoleColor.Cyan;
        if (role == ChatRole.Assistant) return ConsoleColor.Yellow;
        if (role == ChatRole.Tool) return ConsoleColor.Magenta;
        return ConsoleColor.White;
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth - 1, 40, 120);
        }
        catch
        {
            return 80;
        }
    }

    private static string TopBorder(int width) => "┌" + new string('─', width - 2) + "┐";

    private static string BottomBorder(int width) => "└" + new string('─', width - 2) + "┘";

    private static string PanelLine(int width, string text)
    {
        string value = text.Length > width - 4 ? text[..(width - 7)] + "..." : text;
        return "│ " + value.PadRight(width - 4) + " │";
    }

    private static string NextProgressJoke()
    {
        int index = Interlocked.Increment(ref ProgressJokeIndex);
        return ProgressJokes[index % ProgressJokes.Length];
    }

    private static void RenderPermissionChoices(string[] choices, int selectedIndex, int optionTop)
    {
        for (int i = 0; i < choices.Length; i++)
        {
            Console.SetCursorPosition(0, optionTop + i);
            Console.ForegroundColor = i == selectedIndex ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
            string prefix = i == selectedIndex ? "›" : " ";
            Console.Write($"{prefix} {i + 1}. {choices[i]}");
            ClearLineRemainder();
        }

        Console.ResetColor();
    }

    private static void ClearLineRemainder()
    {
        int remaining = Math.Max(0, Console.WindowWidth - Console.CursorLeft - 1);
        if (remaining > 0)
        {
            Console.Write(new string(' ', remaining));
        }
    }

    private static void ReplaceBuffer(List<char> buffer, string value, ref int cursorIndex)
    {
        buffer.Clear();
        buffer.AddRange(value);
        cursorIndex = buffer.Count;
    }

    private static bool TryCycleInlineCompletion(
        List<char> buffer,
        int cursorIndex,
        int delta,
        ref string inlineCompletionKey,
        ref int inlineCompletionIndex)
    {
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out string key, out List<InlineCompletionCandidate> completions) ||
            completions.Count <= 1)
        {
            return false;
        }

        if (!string.Equals(inlineCompletionKey, key, StringComparison.Ordinal))
        {
            inlineCompletionKey = key;
            inlineCompletionIndex = 0;
        }

        inlineCompletionIndex = Mod(inlineCompletionIndex + delta, completions.Count);
        return true;
    }

    private static void NormalizeInlineCompletionCycle(
        List<char> buffer,
        int cursorIndex,
        ref string inlineCompletionKey,
        ref int inlineCompletionIndex)
    {
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out string key, out List<InlineCompletionCandidate> completions))
        {
            inlineCompletionKey = string.Empty;
            inlineCompletionIndex = 0;
            return;
        }

        if (!string.Equals(inlineCompletionKey, key, StringComparison.Ordinal))
        {
            inlineCompletionKey = key;
            inlineCompletionIndex = 0;
            return;
        }

        if (inlineCompletionIndex >= completions.Count)
        {
            inlineCompletionIndex = 0;
        }
    }

    private static bool TryGetInlineCompletion(
        List<char> buffer,
        int cursorIndex,
        int inlineCompletionIndex,
        out InlineCompletionCandidate? completion)
    {
        completion = null;
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out _, out List<InlineCompletionCandidate> completions))
        {
            return false;
        }

        completion = completions[Mod(inlineCompletionIndex, completions.Count)];
        return completion.DisplayText.Length > 0 || completion.ReplacementText.Length > 0;
    }

    private static bool TryGetInlineCompletionCandidates(
        List<char> buffer,
        int cursorIndex,
        out string key,
        out List<InlineCompletionCandidate> completions)
    {
        key = string.Empty;
        completions = [];
        if (cursorIndex != buffer.Count)
        {
            return false;
        }

        string text = new(buffer.ToArray());
        if (TryGetCdArgument(text, out int argumentStartIndex, out string argument))
        {
            if (!TryFindPathCompletions(argument, includeFiles: false, appendDirectorySeparator: false, out List<PathCompletion> argumentCompletions))
            {
                return false;
            }

            key = text;
            bool completeBareCommand = argumentStartIndex == text.Length && text.Equals("/cd", StringComparison.OrdinalIgnoreCase);
            completions = argumentCompletions
                .Select(value => completeBareCommand
                    ? new InlineCompletionCandidate(" " + value.ReplacementText, cursorIndex, " " + value.DisplayText)
                    : new InlineCompletionCandidate(value.ReplacementText, argumentStartIndex, value.DisplayText))
                .Where(value => value.DisplayText.Length > 0 || value.ReplacementText.Length > 0)
                .ToList();
            return completions.Count > 0;
        }

        if (TryGetFileMentionArgument(text, out int mentionArgumentStartIndex, out string mentionArgument))
        {
            if (!TryFindPathCompletions(mentionArgument, includeFiles: true, appendDirectorySeparator: true, out List<PathCompletion> mentionCompletions))
            {
                return false;
            }

            key = text;
            completions = mentionCompletions
                .Select(value => new InlineCompletionCandidate(value.ReplacementText, mentionArgumentStartIndex, value.DisplayText))
                .Where(value => value.DisplayText.Length > 0 || value.ReplacementText.Length > 0)
                .ToList();
            return completions.Count > 0;
        }

        return false;
    }

    private static bool TryGetCdArgument(string text, out int argumentStartIndex, out string argument)
    {
        argumentStartIndex = 0;
        argument = string.Empty;
        if (!text.StartsWith("/cd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == 3)
        {
            argumentStartIndex = text.Length;
            return true;
        }

        if (!char.IsWhiteSpace(text[3]))
        {
            return false;
        }

        argumentStartIndex = 4;
        while (argumentStartIndex < text.Length && char.IsWhiteSpace(text[argumentStartIndex]))
        {
            argumentStartIndex++;
        }

        argument = text[argumentStartIndex..].Trim('"', '\'');
        return true;
    }

    private static bool TryGetFileMentionArgument(string text, out int argumentStartIndex, out string argument)
    {
        argumentStartIndex = 0;
        argument = string.Empty;
        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']);
        tokenStart = tokenStart < 0 ? 0 : tokenStart + 1;
        if (tokenStart >= text.Length || text[tokenStart] != '@')
        {
            return false;
        }

        argumentStartIndex = tokenStart + 1;
        argument = text[(tokenStart + 1)..].Trim('"', '\'');
        return true;
    }

    private static bool TryFindPathCompletions(
        string argument,
        bool includeFiles,
        bool appendDirectorySeparator,
        out List<PathCompletion> completions)
    {
        completions = [];
        string normalizedArgument = argument.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int separatorIndex = normalizedArgument.LastIndexOf(Path.DirectorySeparatorChar);
        string baseArgument = separatorIndex >= 0 ? normalizedArgument[..(separatorIndex + 1)] : string.Empty;
        string namePrefix = separatorIndex >= 0 ? normalizedArgument[(separatorIndex + 1)..] : normalizedArgument;

        string baseDirectory;
        try
        {
            baseDirectory = string.IsNullOrWhiteSpace(baseArgument)
                ? Environment.CurrentDirectory
                : PathResolver.ResolveMentionedPath(baseArgument) ?? Environment.CurrentDirectory;
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(baseDirectory))
        {
            return false;
        }

        var candidates = new List<PathCompletionCandidate>();
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(baseDirectory)
                .Select(path => new PathCompletionCandidate(Path.GetFileName(path), IsDirectory: true))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)));

            if (includeFiles)
            {
                candidates.AddRange(Directory.EnumerateFiles(baseDirectory)
                    .Select(path => new PathCompletionCandidate(Path.GetFileName(path), IsDirectory: false))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }

        if (namePrefix.StartsWith(".", StringComparison.Ordinal))
        {
            candidates.Insert(0, new PathCompletionCandidate("..", IsDirectory: true));
        }

        completions = candidates
            .Where(candidate => candidate.Name is not null &&
                                candidate.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(candidate.Name, namePrefix, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.IsDirectory)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate =>
            {
                string replacementText = baseArgument + candidate.Name;
                if (candidate.IsDirectory && appendDirectorySeparator)
                {
                    replacementText += Path.DirectorySeparatorChar;
                }

                string displayText = replacementText.Length >= normalizedArgument.Length
                    ? replacementText[normalizedArgument.Length..]
                    : string.Empty;
                return new PathCompletion(replacementText, displayText);
            })
            .Where(value => value.DisplayText.Length > 0 || !string.Equals(value.ReplacementText, normalizedArgument, StringComparison.Ordinal))
            .ToList();

        return completions.Count > 0;
    }

    private static void RedrawInputLine(
        List<char> buffer,
        int cursorIndex,
        int inputLeft,
        int inputTop,
        string placeholder,
        int inlineCompletionIndex,
        ref int renderedLength)
    {
        string text = new(buffer.ToArray());
        string completion = string.Empty;
        if (text.Length > 0)
        {
            if (TryGetInlineCompletion(buffer, cursorIndex, inlineCompletionIndex, out InlineCompletionCandidate? candidate) &&
                candidate is not null)
            {
                completion = candidate.DisplayText;
            }
        }

        int currentLength = 1;
        ClearRenderedInputLines(inputLeft, inputTop, Math.Max(renderedLength, currentLength));
        if (text.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            WriteSingleLineText(placeholder, inputLeft, inputTop);
            Console.ResetColor();
            MoveCursor(inputLeft, inputTop);
        }
        else
        {
            InputLineView lineView = GetInputLineView(text, cursorIndex);
            string lineText = lineView.Text;
            bool completionStartsOnCurrentLine = completion.Length > 0 && lineView.EndIndex == text.Length;
            int inputWidth = GetInputWidth(inputLeft);
            int viewStart = GetInputViewStart(lineView.CursorColumn, lineText.Length, inputWidth);
            string visibleText = GetVisibleText(lineText, viewStart, inputWidth);
            int visibleCursorIndex = lineView.CursorColumn - viewStart;
            string visibleCompletion = string.Empty;
            if (completionStartsOnCurrentLine && viewStart + visibleText.Length >= lineText.Length)
            {
                int completionWidth = Math.Max(0, inputWidth - visibleText.Length);
                visibleCompletion = completion.Length > completionWidth ? completion[..completionWidth] : completion;
            }

            Console.ResetColor();
            WriteSingleLineText(visibleText, inputLeft, inputTop);
            if (visibleCompletion.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(visibleCompletion);
                Console.ResetColor();
            }

            MoveCursor(inputLeft, inputTop, visibleCursorIndex);
        }

        renderedLength = currentLength;
    }

    private static int Mod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static void RedrawInputLine(
        List<char> buffer,
        int cursorIndex,
        int inputLeft,
        int inputTop,
        ref int renderedLength)
    {
        string text = new(buffer.ToArray());
        int inputWidth = GetInputWidth(inputLeft);
        int viewStart = GetInputViewStart(cursorIndex, text.Length, inputWidth);
        string visibleText = GetVisibleText(text, viewStart, inputWidth);
        int visibleCursorIndex = cursorIndex - viewStart;
        ClearRenderedInput(inputLeft, inputTop, Math.Max(renderedLength, visibleText.Length));
        Console.SetCursorPosition(inputLeft, inputTop);
        Console.Write(visibleText);

        renderedLength = visibleText.Length;
        MoveCursor(inputLeft, inputTop, visibleCursorIndex);
    }

    private static void MoveCursor(int inputLeft, int inputTop, int cursorIndex)
    {
        int maxLeft = Math.Max(0, Console.BufferWidth - 1);
        Console.SetCursorPosition(Math.Min(inputLeft + cursorIndex, maxLeft), inputTop);
    }

    private static void MoveCursorToInputLineEnd(int inputTop)
    {
        int maxLeft = Math.Max(0, Console.BufferWidth - 1);
        int maxTop = Math.Max(0, Console.BufferHeight - 1);
        Console.SetCursorPosition(maxLeft, Math.Clamp(inputTop, 0, maxTop));
    }

    private static void MoveCursor(int left, int top)
    {
        int maxLeft = Math.Max(0, Console.BufferWidth - 1);
        int maxTop = Math.Max(0, Console.BufferHeight - 1);
        Console.SetCursorPosition(Math.Clamp(left, 0, maxLeft), Math.Clamp(top, 0, maxTop));
    }

    private static int GetInputWidth(int inputLeft)
    {
        return Math.Max(1, Console.BufferWidth - inputLeft - 1);
    }

    private static int GetInputViewStart(int cursorIndex, int textLength, int width)
    {
        if (textLength <= width)
        {
            return 0;
        }

        if (cursorIndex >= textLength)
        {
            return textLength - width;
        }

        return Math.Clamp(cursorIndex - width + 1, 0, textLength - width);
    }

    private static string GetVisibleText(string text, int viewStart, int width)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        int length = Math.Min(width, text.Length - viewStart);
        return text.Substring(viewStart, length);
    }

    private static InputLineView GetInputLineView(string text, int cursorIndex)
    {
        int safeCursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        int lineStart = text.LastIndexOf('\n', Math.Max(0, safeCursorIndex - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        int lineEnd = text.IndexOf('\n', safeCursorIndex);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;

        return new InputLineView(
            text[lineStart..lineEnd],
            lineStart,
            lineEnd,
            safeCursorIndex - lineStart);
    }

    private static bool TryMoveCursorToAdjacentInputLine(List<char> buffer, int direction, ref int cursorIndex)
    {
        if (buffer.Count == 0 || !buffer.Contains('\n'))
        {
            return false;
        }

        string text = new(buffer.ToArray());
        InputLineView currentLine = GetInputLineView(text, cursorIndex);
        if (direction < 0)
        {
            if (currentLine.StartIndex == 0)
            {
                return false;
            }

            int previousLineEnd = currentLine.StartIndex - 1;
            int previousLineStart = text.LastIndexOf('\n', Math.Max(0, previousLineEnd - 1));
            previousLineStart = previousLineStart < 0 ? 0 : previousLineStart + 1;
            cursorIndex = previousLineStart + Math.Min(currentLine.CursorColumn, previousLineEnd - previousLineStart);
            return true;
        }

        if (currentLine.EndIndex >= text.Length)
        {
            return false;
        }

        int nextLineStart = currentLine.EndIndex + 1;
        int nextLineEnd = text.IndexOf('\n', nextLineStart);
        nextLineEnd = nextLineEnd < 0 ? text.Length : nextLineEnd;
        cursorIndex = nextLineStart + Math.Min(currentLine.CursorColumn, nextLineEnd - nextLineStart);
        return true;
    }

    private static void WriteSingleLineText(string text, int inputLeft, int inputTop)
    {
        Console.SetCursorPosition(inputLeft, inputTop);
        Console.Write(text);
    }

    private static void ClearRenderedInput(int inputLeft, int inputTop, int length)
    {
        if (length <= 0)
        {
            return;
        }

        int width = Math.Max(1, Console.BufferWidth);
        int remaining = length;
        int left = inputLeft;
        int top = inputTop;

        while (remaining > 0 && top < Console.BufferHeight)
        {
            int count = Math.Min(remaining, width - left);
            Console.SetCursorPosition(left, top);
            Console.Write(new string(' ', count));
            remaining -= count;
            left = 0;
            top++;
        }
    }

    private static void ClearRenderedInputLines(int inputLeft, int inputTop, int lineCount)
    {
        if (lineCount <= 0)
        {
            return;
        }

        int width = Math.Max(1, Console.BufferWidth);
        for (int line = 0; line < lineCount && inputTop + line < Console.BufferHeight; line++)
        {
            int left = line == 0 ? inputLeft : 0;
            int count = Math.Max(0, width - left);
            Console.SetCursorPosition(left, inputTop + line);
            Console.Write(new string(' ', count));
        }
    }

    private static void EnableBracketedPaste()
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Write("\u001b[?2004h");
        }
    }

    private static void DisableBracketedPaste()
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Write("\u001b[?2004l");
        }
    }

    private static string ReadQueuedEscapeSequence(ConsoleKeyInfo escapeKey)
    {
        var sequence = new StringBuilder().Append(escapeKey.KeyChar);
        int idleTicks = 0;

        while (idleTicks < 20 && sequence.Length < 100_000)
        {
            if (!Console.KeyAvailable)
            {
                idleTicks++;
                Thread.Sleep(1);
                continue;
            }

            idleTicks = 0;
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            sequence.Append(key.Key == ConsoleKey.Enter ? '\n' : key.KeyChar);

            if (sequence.ToString().Contains("\u001b[201~", StringComparison.Ordinal))
            {
                break;
            }
        }

        return sequence.ToString();
    }

    private static bool TryGetBracketedPasteContent(string escapeSequence, out string? content)
    {
        const string startMarker = "\u001b[200~";
        const string endMarker = "\u001b[201~";

        content = null;
        int startIndex = escapeSequence.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return false;
        }

        int contentStartIndex = startIndex + startMarker.Length;
        int endIndex = escapeSequence.IndexOf(endMarker, contentStartIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            content = escapeSequence[contentStartIndex..];
            return true;
        }

        content = escapeSequence[contentStartIndex..endIndex];
        return true;
    }

    private static bool IsShiftEnterSequence(string escapeSequence)
    {
        return escapeSequence is "\u001b[13;2u" or "\u001b[13;2~" or "\u001b[27;2;13~";
    }

    private static string NormalizePastedText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static int MeasureRenderedLineCount(int inputLeft, string text)
    {
        (_, int endTop) = GetRenderedPosition(inputLeft, 0, text, text.Length);
        return endTop + 1;
    }

    private static (int Left, int Top) GetRenderedPosition(int inputLeft, int inputTop, string text, int textIndex)
    {
        int width = Math.Max(1, Console.BufferWidth);
        int left = inputLeft;
        int top = inputTop;
        int safeTextIndex = Math.Clamp(textIndex, 0, text.Length);

        for (int i = 0; i < safeTextIndex; i++)
        {
            AdvanceRenderedPosition(text[i], width, ref left, ref top);
        }

        return (left, top);
    }

    private static void WriteRenderedText(string text, int inputLeft, int inputTop, out int endLeft, out int endTop)
    {
        int width = Math.Max(1, Console.BufferWidth);
        int left = inputLeft;
        int top = inputTop;

        foreach (char value in text)
        {
            if (value == '\r')
            {
                continue;
            }

            if (value == '\n')
            {
                AdvanceRenderedPosition(value, width, ref left, ref top);
                continue;
            }

            MoveCursor(left, top);
            Console.Write(value);
            AdvanceRenderedPosition(value, width, ref left, ref top);
        }

        endLeft = left;
        endTop = top;
    }

    private static void AdvanceRenderedPosition(char value, int width, ref int left, ref int top)
    {
        if (value == '\r')
        {
            return;
        }

        if (value == '\n')
        {
            left = 0;
            top++;
            return;
        }

        if (left >= width - 1)
        {
            left = 0;
            top++;
            return;
        }

        left++;
    }

    private sealed record InlineCompletionCandidate(string ReplacementText, int ReplacementStart, string DisplayText);

    private sealed record InputLineView(string Text, int StartIndex, int EndIndex, int CursorColumn);

    private sealed record PathCompletion(string ReplacementText, string DisplayText);

    private sealed record PathCompletionCandidate(string? Name, bool IsDirectory);

    private sealed class ProgressSpinner : IProgressReporter
    {
        private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
        private readonly object syncRoot;
        private readonly Func<string> nextJoke;
        private readonly CancellationTokenSource cancellation = new();
        private Task? renderTask;
        private bool paused;
        private bool disposed;
        private string message;
        private string joke;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private int progressTop;
        private readonly int reservedLines = 2;
        private readonly string? previousProgressMessage;

        public ProgressSpinner(string message, object syncRoot, Func<string> nextJoke, string? previousProgressMessage)
        {
            this.message = message;
            this.syncRoot = syncRoot;
            this.nextJoke = nextJoke;
            this.previousProgressMessage = previousProgressMessage;
            joke = nextJoke();
            Console.WriteLine();
            Console.WriteLine();
            progressTop = Math.Max(0, Console.CursorTop - reservedLines);
        }

        public void Start()
        {
            renderTask = Task.Run(RenderLoopAsync);
        }

        public void Update(string message)
        {
            lock (syncRoot)
            {
                if (!disposed)
                {
                    this.message = message;
                    activeProgressMessage = message;
                    RecordProgressEvent("progress-update", message);
                }
            }
        }

        public void Pause()
        {
            lock (syncRoot)
            {
                paused = true;
                Clear();
            }
        }

        public void Resume()
        {
            lock (syncRoot)
            {
                if (!disposed)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    progressTop = Math.Max(0, Console.CursorTop - reservedLines);
                    paused = false;
                }
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                ActiveProgress = null;
                activeProgressMessage = previousProgressMessage;
                cancellation.Cancel();
                Clear();
            }

            try
            {
                renderTask?.Wait(TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                // Progress rendering must never affect task execution.
            }

            cancellation.Dispose();
        }

        private async Task RenderLoopAsync()
        {
            int frameIndex = 0;
            int ticksSinceJoke = 0;
            while (!cancellation.IsCancellationRequested)
            {
                lock (syncRoot)
                {
                    if (!paused && !disposed)
                    {
                        if (ticksSinceJoke >= 35)
                        {
                            joke = nextJoke();
                            ticksSinceJoke = 0;
                        }

                        string actionLine = $"{Frames[frameIndex % Frames.Length]} {message} ({FormatElapsed(stopwatch.Elapsed)})";
                        string jokeLine = $"  {joke}";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        WriteProgressLine(0, actionLine);
                        WriteProgressLine(1, jokeLine);
                        Console.SetCursorPosition(0, progressTop + reservedLines);
                        Console.ResetColor();
                    }
                }

                frameIndex++;
                ticksSinceJoke++;
                await Task.Delay(140, cancellation.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        private void Clear()
        {
            for (int i = 0; i < reservedLines; i++)
            {
                WriteProgressLine(i, string.Empty);
            }

            Console.SetCursorPosition(0, progressTop);
        }

        private void WriteProgressLine(int lineOffset, string value)
        {
            int width = Console.WindowWidth > 1 ? Console.WindowWidth - 1 : 1;
            int top = progressTop + lineOffset;
            if (top >= Console.BufferHeight)
            {
                return;
            }

            string clipped = value.Length > width ? value[..width] : value;
            Console.SetCursorPosition(0, top);
            Console.Write(clipped.PadRight(width));
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"m\:ss");
    }

    private sealed class ProgressSuspension(ProgressSpinner spinner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            spinner.Resume();
        }
    }

    private sealed class NoopDisposable : IProgressReporter
    {
        public void Update(string message)
        {
            activeProgressMessage = message;
            RecordProgressEvent("progress-update", message);
        }

        public void Dispose()
        {
        }

    }

    private sealed class ProgressScope(IProgressReporter inner, string? previousProgressMessage) : IProgressReporter
    {
        public void Update(string message) => inner.Update(message);

        public void Dispose()
        {
            inner.Dispose();
            activeProgressMessage = previousProgressMessage;
            RecordProgressEvent("progress-end");
        }
    }
}
