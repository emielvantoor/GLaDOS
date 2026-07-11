using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;

namespace Potato.Session.Tasks;

public abstract class AgentTaskBase
{
    protected abstract string Name { get; }

    public string ActionName => StringHelper.NormalizeAction(Name);

    public virtual IReadOnlyList<string> PlanningGuidance => [];
    
    public bool CanExecute(string targetAction)
    {
        return string.Equals(ActionName, StringHelper.NormalizeAction(targetAction), StringComparison.InvariantCultureIgnoreCase);
    }
    
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    
    public static ChatOptions CreateChatOptions(double temperature) =>
        new()
        {
            Temperature = (float)temperature
        };

    public static ChatOptions CreateJsonChatOptions(double temperature) =>
        new()
        {
            Temperature = (float)temperature,
            ResponseFormat = ChatResponseFormat.Json
        };
    
    protected SearchReplacePatch ParseSearchReplaceBlocks(string text)
    {
        string normalized = StringHelper.StripCodeFence(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        Match match = Regex.Match(
            normalized,
            @"<SEARCH>\n?(?<search>.*?)\n?</SEARCH>\s*<REPLACE>\n?(?<replace>.*?)\n?</REPLACE>",
            RegexOptions.Singleline);

        if (!match.Success)
        {
            throw new InvalidOperationException("Patch model did not return <SEARCH>/<REPLACE> blocks.");
        }

        return new SearchReplacePatch
        {
            Search = match.Groups["search"].Value,
            Replace = match.Groups["replace"].Value
        };
    }
}
