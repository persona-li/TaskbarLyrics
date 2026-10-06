namespace TaskbarLyrics.QQMusic.Models;

public sealed class HttpDiagnostics
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool Success => StatusCode is >= 200 and < 300;
    public int Attempt { get; init; }

    public string BodyPreview(int maxChars = 1000)
    {
        if (string.IsNullOrEmpty(Body))
        {
            return string.Empty;
        }

        return Body.Length <= maxChars ? Body : Body[..maxChars] + "...";
    }
}

public sealed class SearchServiceResult
{
    public bool Success { get; set; }
    public int? ApiCode { get; set; }
    public List<QQSongCandidate> Candidates { get; set; } = new();
    public HttpDiagnostics? Http { get; set; }
    public string? RawJson { get; set; }
    public string? Error { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public bool HasMore { get; set; }
}
