using Microsoft.Extensions.AI;

internal static class PotatoConsole
{
    private const string PromptText = "> ";

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

    public static void WritePrompt()
    {
        WriteSeparator();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(PromptText);
        Console.ResetColor();
    }

    public static string? ReadPromptInput(IReadOnlyList<string> history)
    {
        WritePrompt();

        var buffer = new List<char>();
        int cursorIndex = 0;
        int historyIndex = history.Count;
        string draftInput = string.Empty;
        int inputLeft = Console.CursorLeft;
        int inputTop = Console.CursorTop;
        int renderedLength = 0;

        while (true)
        {
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
                        MoveCursor(inputLeft, inputTop, cursorIndex);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorIndex < buffer.Count)
                    {
                        cursorIndex++;
                        MoveCursor(inputLeft, inputTop, cursorIndex);
                    }
                    break;

                case ConsoleKey.Home:
                    cursorIndex = 0;
                    MoveCursor(inputLeft, inputTop, cursorIndex);
                    break;

                case ConsoleKey.End:
                    cursorIndex = buffer.Count;
                    MoveCursor(inputLeft, inputTop, cursorIndex);
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
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (history.Count > 0 && historyIndex < history.Count)
                    {
                        historyIndex++;
                        string value = historyIndex == history.Count ? draftInput : history[historyIndex];
                        ReplaceBuffer(buffer, value, ref cursorIndex);
                        RedrawInputLine(buffer, cursorIndex, inputLeft, inputTop, ref renderedLength);
                    }
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
                        MoveCursor(inputLeft, inputTop, cursorIndex);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorIndex < buffer.Count)
                    {
                        cursorIndex++;
                        MoveCursor(inputLeft, inputTop, cursorIndex);
                    }
                    break;

                case ConsoleKey.Home:
                    cursorIndex = 0;
                    MoveCursor(inputLeft, inputTop, cursorIndex);
                    break;

                case ConsoleKey.End:
                    cursorIndex = buffer.Count;
                    MoveCursor(inputLeft, inputTop, cursorIndex);
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
        Console.WriteLine("  @path/to/file   Attach a text file to your next message");
        Console.WriteLine("  /model          Show model selection and switch models");
        Console.WriteLine("  /cd [path]      Change the current working directory");
        Console.WriteLine("  /ask question   Ask a side question without changing chat history");
        Console.WriteLine("  /transcript     Show the current conversation sent to the model");
        Console.WriteLine("  /abort          Cancel the current task and return to the main prompt");
        Console.WriteLine("  Ctrl+C          Abort the in-flight task; exits normally at the idle prompt");
        Console.WriteLine("  Up/Down         Cycle through commands entered in this session");
        Console.WriteLine("  exit, quit      Close Potato Code");
        Console.WriteLine("  y, yes, ok      Approve the current specification");
        Console.WriteLine("  execute         Approve risky or multi-step execution");
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

    public static void WriteModelExchange(int iteration, string prompt, string response)
    {
        WriteBoxHeader($"ReAct conversation {iteration}");
        WriteLabeledBlock("Sent to model", prompt, ConsoleColor.Cyan, maxLines: 10);
        WriteLabeledBlock("Model replied", response, ConsoleColor.Yellow, maxLines: null);
        WriteBoxFooter();
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

    public static void WriteModelQuestion(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Model question:");
        Console.WriteLine(message);
        Console.ResetColor();
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

    private static void ReplaceBuffer(List<char> buffer, string value, ref int cursorIndex)
    {
        buffer.Clear();
        buffer.AddRange(value);
        cursorIndex = buffer.Count;
    }

    private static void RedrawInputLine(
        List<char> buffer,
        int cursorIndex,
        int inputLeft,
        int inputTop,
        ref int renderedLength)
    {
        string text = new(buffer.ToArray());
        Console.SetCursorPosition(inputLeft, inputTop);
        Console.Write(text);

        if (renderedLength > text.Length)
        {
            Console.Write(new string(' ', renderedLength - text.Length));
        }

        renderedLength = text.Length;
        MoveCursor(inputLeft, inputTop, cursorIndex);
    }

    private static void MoveCursor(int inputLeft, int inputTop, int cursorIndex)
    {
        int maxLeft = Math.Max(0, Console.BufferWidth - 1);
        Console.SetCursorPosition(Math.Min(inputLeft + cursorIndex, maxLeft), inputTop);
    }
}
