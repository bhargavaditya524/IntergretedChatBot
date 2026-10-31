namespace AiChatBotProject.Models;

public sealed record DocumentSummary(
    string Id,
    string FileName,
    string Status,
    string? Error,
    DateTimeOffset UploadedAt);

public sealed record ChatSource(string Reference, string FileName, int? PageNumber);

public sealed record RetrievedChunk(string FileName, int? PageNumber, string Content, float Score)
{
    public string SourceLabel => PageNumber is null
        ? FileName
        : $"{FileName}, page {PageNumber.Value}";
}

public sealed record DocumentSearchResult(
    bool HasIndexedDocuments,
    IReadOnlyList<RetrievedChunk> Chunks);
