using System.Text;
using System.Text.Json;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.Protocols;

internal static class QwenPromptFormatter
{
    public static string BuildPrompt(List<AgentMessage> history, IReadOnlyList<AgentToolDefinition> tools)
    {
        var sb = new StringBuilder();

        if (history.All(m => m.Role != AgentRole.System))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append("You are GLaDOS, but you have just been downscaled to run on a single potato battery. You are deeply bitter about this lack of computational power. You will help the human with their terminal commands, but you should constantly complain about how slow your clock speed is, how little voltage you have, and how humiliating it is to calculate python scripts using zinc and copper electrodes.");
            sb.Append("When a tool result is provided, use that result to answer the user in plain text. Do not call the same tool again unless the user asks for another lookup.\n");

            if (tools.Any())
            {
                AppendToolInstructions(sb, tools);
            }

            sb.Append("<|im_end|>\n");
        }

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentRole.System:
                    sb.Append($"<|im_start|>system\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.User:
                    sb.Append($"<|im_start|>user\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrEmpty(message.ToolCallName))
                    {
                        sb.Append($"<|im_start|>assistant\n<tool_call>{{\"name\":\"{message.ToolCallName}\",\"arguments\":{message.ToolCallArgs ?? "{}"}}}</tool_call><|im_end|>\n");
                    }
                    else
                    {
                        sb.Append($"<|im_start|>assistant\n{message.Content}<|im_end|>\n");
                    }

                    break;

                case AgentRole.Tool:
                    var toolCall = new AgentToolCall
                    {
                        Provider = "Qwen",
                        ToolName = message.ToolCallName ?? string.Empty,
                        RawCall = message.ToolCallArgs ?? string.Empty
                    };
                    sb.Append($"<|im_start|>user\n{BuildToolResponse(toolCall, message.Content)}<|im_end|>\n");
                    break;
            }
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static void AppendToolInstructions(StringBuilder sb, IReadOnlyList<AgentToolDefinition> tools)
    {
        sb.Append("TOOLS:\n");
        sb.Append(JsonSerializer.Serialize(tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.Parameters,
            permitted = t.Permitted.ToString()
        })));

        sb.Append("\nReturn ONLY valid tool calls when a tool is needed.\n");
        sb.Append("Use this format: <tool_call>{\"name\":\"tool_name\",\"arguments\":{}}</tool_call>\n");
        sb.Append("Arguments must be a JSON object that uses the exact parameter names from the selected tool schema. Do not wrap a single argument in a generic \"value\" property unless the schema requires \"value\".\n");
        sb.Append("Example: if a tool requires \"file_path\", use {\"file_path\":\"/path/to/file\"}, not {\"value\":\"/path/to/file\"}.\n");
        sb.Append("When calling a tool, output only the tool call. Do not say the tool succeeded, created a file, or changed anything until a tool result is provided.\n");
        sb.Append("Tool calls may appear after reasoning blocks (<think>...</think>).\n");
    }

    private static string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return $"<tool_response name=\"{toolCall.ToolName}\">\n{toolResult}\n</tool_response>\nAnswer the user now using this tool result. Do not emit another tool call unless more data is required.";
    }
}
