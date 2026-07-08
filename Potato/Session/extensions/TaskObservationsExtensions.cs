using System.Text;
using Potato.Session.Models;

namespace Potato.Session.extensions;

public static class TaskObservationsExtensions
{
    public static string FormatObservations(this IEnumerable<TaskObservation> observations)
    {
        var builder = new StringBuilder();
        foreach (TaskObservation observation in observations)
        {
            builder.AppendLine($"Step {observation.Step} {observation.Action} {observation.Argument}:");
            builder.AppendLine(Truncate(observation.Result, 4_000));
            builder.AppendLine();
        }

        return builder.Length == 0 ? "(none)" : builder.ToString();
    }
    
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}