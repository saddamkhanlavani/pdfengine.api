using System;
using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface IEmailService
{
    Task SendVerificationEmailAsync(User user, string token);
    Task SendWelcomeEmailAsync(User user);
    Task SendApiKeyCreatedEmailAsync(User user, string keyName);
    Task SendTeamInvitationEmailAsync(string email, string tenantName, string token);
    Task SendPasswordResetEmailAsync(User user, string token);
    Task SendUsageThresholdAlertAsync(Tenant tenant, string userEmail, int percentage);
    Task SendWebhookFailureAlertAsync(Tenant tenant, string userEmail, string endpointUrl, string error);
    Task SendRenderFailureAlertAsync(Tenant tenant, string userEmail, string jobId, string error);
}
