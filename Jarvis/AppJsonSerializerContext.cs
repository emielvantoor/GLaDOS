using System.Text.Json.Serialization;
using Jarvis.Models;

[JsonSerializable(typeof(Todo[]))]
[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(ChatChoice[]))]
[JsonSerializable(typeof(ChatChunkChoice))]
[JsonSerializable(typeof(ChatChunkChoice[]))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ChatCompletionChunk[]))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(RuntimeMemoryUsageResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
