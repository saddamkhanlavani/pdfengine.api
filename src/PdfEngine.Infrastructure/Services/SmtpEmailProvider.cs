using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PdfEngine.Application.Configurations;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class SmtpEmailProvider : IEmailProvider
{
    private readonly EmailOptions _options;

    public SmtpEmailProvider(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var smtpSettings = _options.Smtp;
        using var client = new SmtpClient(smtpSettings.Host, smtpSettings.Port)
        {
            EnableSsl = smtpSettings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrEmpty(smtpSettings.Username))
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(smtpSettings.Username, smtpSettings.Password);
        }

        var mailMessage = new MailMessage
        {
            From = new MailAddress(_options.SenderEmail, _options.SenderName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        mailMessage.To.Add(to);

        await client.SendMailAsync(mailMessage);
    }
}
