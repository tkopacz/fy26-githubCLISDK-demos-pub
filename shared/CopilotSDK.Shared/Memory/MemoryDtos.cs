using System.Text.Json.Serialization;

namespace CopilotSDK.Demos.Shared.Memory;

public record MemoryListResponse(
    // Kształt DTO zwracany przez punkt końcowy REST pamięci platformy GitHub Copilot.
    // Te rekordy modelują fakty trwałej pamięci, a nie zdarzenia SDK dotyczące sesji.
    [property: JsonPropertyName("memories")] List<MemoryItem>? Memories,
    [property: JsonPropertyName("total_count")] int? TotalCount);

public record MemoryItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("created_at")] DateTime? CreatedAt,
    [property: JsonPropertyName("score")] double? Score);
