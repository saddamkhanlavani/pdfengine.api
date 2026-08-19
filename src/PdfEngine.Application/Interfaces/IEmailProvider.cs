using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IEmailProvider
{
    Task SendEmailAsync(string to, string subject, string body);
}
