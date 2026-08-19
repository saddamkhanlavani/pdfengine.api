using System;

namespace PdfEngine.Application.Configurations;

public class EmailOptions
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "Log"; // "Log", "SMTP", "SendGrid", "Postmark"
    public string SenderEmail { get; set; } = "no-reply@pdfengine.com";
    public string SenderName { get; set; } = "PDFEngine";

    public SmtpOptions Smtp { get; set; } = new();
    public SendGridOptions SendGrid { get; set; } = new();
    public PostmarkOptions Postmark { get; set; } = new();
}

public class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = false;
}

public class SendGridOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public class PostmarkOptions
{
    public string ServerToken { get; set; } = string.Empty;
}
