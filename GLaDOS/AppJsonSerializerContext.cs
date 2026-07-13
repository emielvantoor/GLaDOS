using System.Text.Json.Serialization;
using GLaDOS.Models;

[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(ChatChoice[]))]
[JsonSerializable(typeof(ChatChunkChoice))]
[JsonSerializable(typeof(ChatChunkChoice[]))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ChatCompletionChunk[]))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(FimCompletionChoice))]
[JsonSerializable(typeof(FimCompletionChoice[]))]
[JsonSerializable(typeof(FimCompletionChunk))]
[JsonSerializable(typeof(FimCompletionChunk[]))]
[JsonSerializable(typeof(FimCompletionChunkChoice))]
[JsonSerializable(typeof(FimCompletionChunkChoice[]))]
[JsonSerializable(typeof(FimCompletionRequest))]
[JsonSerializable(typeof(FimCompletionResponse))]
[JsonSerializable(typeof(RuntimeMemoryUsageResponse))]
[JsonSerializable(typeof(PotatoSessionSummary))]
[JsonSerializable(typeof(PotatoSessionSummary[]))]
[JsonSerializable(typeof(PotatoSessionDetail))]
[JsonSerializable(typeof(PotatoSessionEvent))]
[JsonSerializable(typeof(PotatoSessionEvent[]))]
[JsonSerializable(typeof(PotatoSessionStartRequest))]
[JsonSerializable(typeof(PotatoSessionEventRequest))]
[JsonSerializable(typeof(PotatoSessionInputRequest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
