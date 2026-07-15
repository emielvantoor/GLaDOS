using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Potato;

/// <summary>
/// Simple token counting for measuring chat history and context optimization effectiveness.
/// Uses a rough heuristic: average 4 characters per token (reasonable for English code).
/// For precise counts, integrate with OpenAI's cl100k_base tokenizer or similar.
/// </summary>
internal sealed class TokenCounter
{
    private const double CharsPerToken = 4.0;

    public sealed class SessionMetrics
    {
        public int ChatHistoryCharacters { get; set; }
        public int ChatHistoryEstimatedTokens { get; set; }
        
        public int ExecutionMemoryCharacters { get; set; }
        public int ExecutionMemoryEstimatedTokens { get; set; }
        
        public int TruncationCount { get; set; }
        public int TotalBytesRecovered { get; set; }
        public double TokensSavedPercentage { get; set; }
    }

    public static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        
        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    public static SessionMetrics CalculateMetrics(
        IReadOnlyList<ChatMessage> chatHistory,
        ExecutionMemory executionMemory)
    {
        var chatHistoryText = new System.Text.StringBuilder();
        foreach (var message in chatHistory)
        {
            chatHistoryText.Append(message.Text ?? "");
            foreach (var content in message.Contents)
            {
                if (content is Microsoft.Extensions.AI.TextContent textContent)
                {
                    chatHistoryText.Append(textContent.Text ?? "");
                }
            }
        }

        int chatChars = chatHistoryText.Length;
        int chatTokens = EstimateTokenCount(chatHistoryText.ToString());

        // ExecutionMemory is only partially accessible via GetCollectedContext
        // We estimate it as "recovered" tokens (data not in chat history)
        int memoryChars = 0;
        int truncatedItems = 0;
        int bytesRecovered = 0;

        // This is a simplified calculation; a real implementation would 
        // iterate ExecutionMemory items and sum them
        
        double tokensSaved = chatTokens > 0 ? (bytesRecovered / CharsPerToken) / chatTokens * 100.0 : 0;

        return new SessionMetrics
        {
            ChatHistoryCharacters = chatChars,
            ChatHistoryEstimatedTokens = chatTokens,
            ExecutionMemoryCharacters = memoryChars,
            ExecutionMemoryEstimatedTokens = (int)Math.Ceiling(memoryChars / CharsPerToken),
            TruncationCount = truncatedItems,
            TotalBytesRecovered = bytesRecovered,
            TokensSavedPercentage = tokensSaved
        };
    }

    public static string FormatMetricsReport(SessionMetrics metrics)
    {
        return $"""
            === Token Optimization Report ===
            Chat History:
              - Characters: {metrics.ChatHistoryCharacters:N0}
              - Est. Tokens: {metrics.ChatHistoryEstimatedTokens:N0}
            
            Execution Memory (Full Data):
              - Characters: {metrics.ExecutionMemoryCharacters:N0}
              - Est. Tokens: {metrics.ExecutionMemoryEstimatedTokens:N0}
            
            Optimization:
              - Truncations: {metrics.TruncationCount}
              - Bytes Recovered: {metrics.TotalBytesRecovered:N0}
              - Tokens Saved: ~{metrics.TokensSavedPercentage:F1}%
            
            Effective token usage saved truncated data in ExecutionMemory for retrieval via GetCollectedContext.
            """;
    }
}
