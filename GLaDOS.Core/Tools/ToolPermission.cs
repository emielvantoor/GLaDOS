using System.Text.Json.Serialization;

namespace GLaDOS.Core.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<ToolPermission>))]
public enum ToolPermission
{
    Automatic,
    User
}
