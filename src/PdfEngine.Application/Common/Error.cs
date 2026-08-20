namespace PdfEngine.Application.Common;

public struct Error
{
    public string Code { get; }
    public string Message { get; }

    public Error(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error Validation(string message) => new(ErrorCodes.Validation, message);
    public static Error HtmlTooLarge(string message) => new(ErrorCodes.HtmlTooLarge, message);
    public static Error RenderTimeout(string message) => new(ErrorCodes.RenderTimeout, message);
    public static Error DependencyUnavailable(string message) => new(ErrorCodes.DependencyUnavailable, message);
    public static Error BrowserUnavailable(string message) => new(ErrorCodes.BrowserUnavailable, message);
    public static Error Internal(string message) => new(ErrorCodes.Internal, message);
    public static Error RequestAborted(string message) => new(ErrorCodes.RequestAborted, message);
    public static Error NotFound(string message) => new(ErrorCodes.NotFound, message);
    public static Error QuotaExceededError(string message) => new(ErrorCodes.QuotaExceeded, message);
    public static Error BlockedUrl(string message) => new(ErrorCodes.BlockedUrl, message);
}

public static class ErrorCodes
{
    public const string Validation = "VALIDATION_ERROR";
    public const string HtmlTooLarge = "HTML_TOO_LARGE";
    public const string RenderTimeout = "RENDER_TIMEOUT";
    public const string BrowserUnavailable = "BROWSER_UNAVAILABLE";
    public const string Internal = "INTERNAL_ERROR";
    public const string RequestAborted = "REQUEST_ABORTED";
    public const string NotFound = "NOT_FOUND";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string BlockedUrl = "BLOCKED_URL";
    /// <summary>A backing service (Redis, the database, object storage) is unreachable.
    /// Distinct from INTERNAL_ERROR because the caller's correct response is different:
    /// this one is retryable and the engine can say roughly when.</summary>
    public const string DependencyUnavailable = "DEPENDENCY_UNAVAILABLE";
}
