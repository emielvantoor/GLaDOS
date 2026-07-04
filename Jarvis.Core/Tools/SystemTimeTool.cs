using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Core.Tools;

public class SystemTimeTool : IJarvisTool
{
    public string Name => "get_system_time";

    public string Description =>
        "Retrieves the current local system date, time, and day of the week. Use this whenever the user asks for the current time or date.";

    public ToolPermission Permitted => ToolPermission.Automatic;

    // Dit geeft nu netjes het OpenAI-compatibele schema terug als JsonObject
    public JsonObject Parameters => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject() // Geen argumenten nodig voor de klok
    };

    public Task<string> ExecuteAsync(JsonObject arguments)
    {
        var now = DateTime.Now;

        // InvariantCulture dwingt Engels af (bijv. "Thursday" i.p.v. "donderdag")
        string dayOfWeek = now.ToString("dddd", CultureInfo.InvariantCulture);
        string dateStr = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string timeStr = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var resultObj = new
        {
            current_time = timeStr,
            current_date = dateStr,
            day_of_the_week = dayOfWeek,
            timezone = TimeZoneInfo.Local.DisplayName
        };

        // We returnen dit als een strakke JSON string waar Qwen feilloos mee verder kan
        string jsonResult = JsonSerializer.Serialize(resultObj);

        return Task.FromResult(jsonResult);
    }
}
