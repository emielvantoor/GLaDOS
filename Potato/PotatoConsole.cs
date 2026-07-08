using Microsoft.Extensions.AI;

namespace Potato;

internal static class PotatoConsole
{
    private const string PromptText = "> ";
    private const string DefaultPromptPlaceholder = "Type your message or @path/to/file";
    private static readonly object ProgressLock = new();
    private static ProgressSpinner? ActiveProgress;
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
        "Stand by. Science is happening, or something with similar indentation."
    ];
    private static int ProgressJokeIndex;

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

    public static string? ReadPromptInput(IReadOnlyList<string> history, string? placeholder = null)
    {
        placeholder = string.IsNullOrWhiteSpace(placeholder) ? DefaultPromptPlaceholder : placeholder;
        (int inputLeft, int inputTop, int placeholderLength) = WritePrompt(placeholder);

        var buffer = new List<char>();
        int cursorIndex = 0;
        int historyIndex = history.Count;
        string draftInput = string.Empty;
        int renderedLength = placeholderLength;
        int inlineCompletionIndex = 0;
        string inlineCompletionKey = string.Empty;

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    NormalizeInlineCompletionCycle(buffer, cursorIndex, ref inlineCompletionKey, ref inlineCompletionIndex);
                    if (TryGetInlineCompletion(buffer, cursorIndex, inlineCompletionIndex, out string completion))
                    {
                        buffer.InsertRange(cursorIndex, completion);
                        cursorIndex += completion.Length;
                        inlineCompletionIndex = 0;
                        inlineCompletionKey = string.Empty;
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, placeholder, inlineCompletionIndex, ref renderedLength);
                        break;
                    }

                    Console.WriteLine();
                    WriteSeparator();
                    Console.WriteLine();
                    return new string(buffer.ToArray());

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
                    if (history.Count > 0 && historyIndex > 0)
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
                    if (history.Count > 0 && historyIndex < history.Count)
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
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Shortcuts:");
        Console.WriteLine("  @path/to/file   Attach a file; Left/Right cycle path completions");
        Console.WriteLine("  /model          Show model selection and switch models");
        Console.WriteLine("  /cd path        Change directory; Left/Right cycle completions, Enter accepts");
        Console.WriteLine("  /ask question   Ask a side question without changing chat history");
        Console.WriteLine("  /prompts        Show or change prompt source: status, defaults, external");
        Console.WriteLine("  /sessions       List tracked sessions");
        Console.WriteLine("  /transcript     Show or save a tracked session transcript");
        Console.WriteLine("  /abort          Cancel the current task and return to the main prompt");
        Console.WriteLine("  Ctrl+C          Abort the in-flight task; exits normally at the idle prompt");
        Console.WriteLine("  Up/Down         Cycle through commands entered in this session");
        Console.WriteLine("  exit, quit      Close Potato Code");
        Console.WriteLine("  y, yes, ok      Approve the current specification");
        Console.WriteLine("  execute         Approve a reviewed plan for execution");
        Console.WriteLine("  abort           Cancel a reviewed plan before execution");
        Console.ResetColor();
    }

    public static void WriteAgentResponse(string text)
    {
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
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteStatus(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static IProgressReporter StartProgress(string message)
    {
        if (Console.IsOutputRedirected)
        {
            return new NoopDisposable();
        }

        lock (ProgressLock)
        {
            ActiveProgress?.Dispose();
            ActiveProgress = new ProgressSpinner(message, ProgressLock, NextProgressJoke);
            ActiveProgress.Start();
            return ActiveProgress;
        }
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

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  1. Yes, allow once");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  2. Yes, allow always (default)");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  3. No, suggest changes (esc)");
        Console.ResetColor();
        Console.WriteLine();
        Console.Write("Choice [1/2/3, Enter=2]: ");

        while (true)
        {
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

                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return ToolPermissionChoice.AllowAlways;
            }
        }
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
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out string key, out List<string> completions) ||
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
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out string key, out List<string> completions))
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
        out string completion)
    {
        completion = string.Empty;
        if (!TryGetInlineCompletionCandidates(buffer, cursorIndex, out _, out List<string> completions))
        {
            return false;
        }

        completion = completions[Mod(inlineCompletionIndex, completions.Count)];
        return completion.Length > 0;
    }

    private static bool TryGetInlineCompletionCandidates(
        List<char> buffer,
        int cursorIndex,
        out string key,
        out List<string> completions)
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
            if (!TryFindPathCompletions(argument, includeFiles: false, appendDirectorySeparator: false, out List<string> argumentCompletions))
            {
                return false;
            }

            key = text;
            bool completeBareCommand = argumentStartIndex == text.Length && text.Equals("/cd", StringComparison.OrdinalIgnoreCase);
            completions = argumentCompletions
                .Select(value => completeBareCommand ? " " + value : value)
                .Where(value => value.Length > 0)
                .ToList();
            return completions.Count > 0;
        }

        if (TryGetFileMentionArgument(text, out string mentionArgument))
        {
            if (!TryFindPathCompletions(mentionArgument, includeFiles: true, appendDirectorySeparator: true, out completions))
            {
                return false;
            }

            key = text;
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

    private static bool TryGetFileMentionArgument(string text, out string argument)
    {
        argument = string.Empty;
        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']);
        tokenStart = tokenStart < 0 ? 0 : tokenStart + 1;
        if (tokenStart >= text.Length || text[tokenStart] != '@')
        {
            return false;
        }

        argument = text[(tokenStart + 1)..].Trim('"', '\'');
        return true;
    }

    private static bool TryFindPathCompletions(
        string argument,
        bool includeFiles,
        bool appendDirectorySeparator,
        out List<string> completions)
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
                string value = candidate.Name![namePrefix.Length..];
                return candidate.IsDirectory && appendDirectorySeparator
                    ? value + Path.DirectorySeparatorChar
                    : value;
            })
            .Where(value => value.Length > 0)
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
            TryGetInlineCompletion(buffer, cursorIndex, inlineCompletionIndex, out completion);
        }

        int currentLength = text.Length == 0
            ? placeholder.Length
            : text.Length + completion.Length;
        int inputWidth = GetInputWidth(inputLeft);
        int viewStart = GetInputViewStart(cursorIndex, text.Length, inputWidth);
        string visibleText = GetVisibleText(text, viewStart, inputWidth);
        int visibleCursorIndex = cursorIndex - viewStart;
        string visibleCompletion = string.Empty;
        if (completion.Length > 0 && viewStart + visibleText.Length >= text.Length)
        {
            int completionWidth = Math.Max(0, inputWidth - visibleText.Length);
            visibleCompletion = completion.Length > completionWidth ? completion[..completionWidth] : completion;
        }

        currentLength = text.Length == 0
            ? Math.Min(placeholder.Length, inputWidth)
            : visibleText.Length + visibleCompletion.Length;
        ClearRenderedInput(inputLeft, inputTop, Math.Max(renderedLength, currentLength));
        Console.SetCursorPosition(inputLeft, inputTop);
        if (text.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(placeholder.Length > inputWidth ? placeholder[..inputWidth] : placeholder);
            Console.ResetColor();
        }
        else
        {
            Console.ResetColor();
            Console.Write(visibleText);
            if (visibleCompletion.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(visibleCompletion);
                Console.ResetColor();
            }
        }

        renderedLength = currentLength;
        MoveCursor(inputLeft, inputTop, visibleCursorIndex);
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
        private readonly int progressTop;
        private readonly int reservedLines = 2;

        public ProgressSpinner(string message, object syncRoot, Func<string> nextJoke)
        {
            this.message = message;
            this.syncRoot = syncRoot;
            this.nextJoke = nextJoke;
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

                        string actionLine = $"{Frames[frameIndex % Frames.Length]} {message}";
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
        }

        public void Dispose()
        {
        }
    }
}
