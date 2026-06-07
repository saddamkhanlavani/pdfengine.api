namespace PdfEngine.Domain.Enums;

public enum PdfJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    DeadLetter,
    Cancelled,
    Expired
}
