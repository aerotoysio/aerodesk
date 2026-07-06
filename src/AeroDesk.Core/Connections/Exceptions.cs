using System.Net;

namespace AeroDesk.Core.Connections;

/// <summary>Thrown when the DocumentForge server answers with a non-success status;
/// carries the parsed error message when the body had one.</summary>
public sealed class DfHttpException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public DfHttpException(HttpStatusCode statusCode, string message) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// Optimistic-concurrency failure (HTTP 412): the document changed since we read it.
/// Carries both ETags so the UI can offer "reload and retry".
/// </summary>
public sealed class EtagConflictException : Exception
{
    public string? ExpectedEtag { get; }
    public string? ActualEtag { get; }

    public EtagConflictException(string? expected, string? actual, string message) : base(message)
    {
        ExpectedEtag = expected;
        ActualEtag = actual;
    }
}
