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
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
