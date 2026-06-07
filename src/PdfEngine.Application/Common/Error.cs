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
    public static Error BrowserUnavailable(string message) => new(ErrorCodes.BrowserUnavailable, message);
    public static Error Internal(string message) => new(ErrorCodes.Internal, message);
    public static Error RequestAborted(string message) => new(ErrorCodes.RequestAborted, message);
    public static Error NotFound(string message) => new(ErrorCodes.NotFound, message);
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
}
