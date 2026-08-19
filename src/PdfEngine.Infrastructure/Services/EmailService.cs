using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IEmailProvider _emailProvider;
    private readonly PdfEngineDbContext _dbContext;
    private readonly ILogger<EmailService> _logger;
    private const string ClientUrl = "http://localhost:3001";

    public EmailService(
        IEmailProvider emailProvider,
        PdfEngineDbContext dbContext,
        ILogger<EmailService> logger)
    {
        _emailProvider = emailProvider;
        _dbContext = dbContext;
        _logger = logger;
    }

    private async Task LogAndSendEmailAsync(Guid tenantId, string toEmail, string subject, string body, string type)
    {
        try
        {
            // Send the email first
            await _emailProvider.SendEmailAsync(toEmail, subject, body);

            // Log it in the database
            var emailLog = new EmailLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ToEmail = toEmail,
                Subject = subject,
                Body = body,
                Type = type,
                SentAt = DateTime.UtcNow
            };

            _dbContext.EmailLogs.Add(emailLog);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send or log email of type {Type} to {ToEmail}", type, toEmail);
            throw;
        }
    }

    private string GetHtmlTemplate(string title, string contentHtml, string ctaText = "", string ctaUrl = "")
    {
        var buttonHtml = string.IsNullOrEmpty(ctaText) || string.IsNullOrEmpty(ctaUrl)
            ? ""
            : $@"<div style=""text-align: center; margin: 30px 0;"">
                    <a href=""{ctaUrl}"" style=""background-color: #0f172a; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 14px; display: inline-block; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);"">{ctaText}</a>
                 </div>";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title}</title>
</head>
<body style=""margin: 0; padding: 0; background-color: #f8fafc; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; -webkit-font-smoothing: antialiased; -moz-osx-font-smoothing: grayscale; color: #334155;"">
    <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""width: 100%; border-collapse: collapse; background-color: #f8fafc; padding: 40px 0;"">
        <tr>
            <td align=""center"">
                <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""width: 100%; max-width: 600px; border-collapse: collapse; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.05), 0 4px 6px -4px rgba(0, 0, 0, 0.05); border: 1px solid #e2e8f0; margin: 40px 0;"">
                    <!-- Header -->
                    <tr style=""background-color: #0f172a; text-align: center;"">
                        <td style=""padding: 30px 0;"">
                            <span style=""color: #ffffff; font-size: 24px; font-weight: 800; letter-spacing: -0.05em;"">PDF<span style=""color: #38bdf8;"">Engine</span></span>
                        </td>
                    </tr>
                    <!-- Content -->
                    <tr>
                        <td style=""padding: 40px 30px;"">
                            <h1 style=""font-size: 20px; font-weight: 700; color: #0f172a; margin-top: 0; margin-bottom: 20px; text-align: left;"">{title}</h1>
                            <div style=""font-size: 15px; line-height: 1.6; color: #475569;"">
                                {contentHtml}
                            </div>
                            {buttonHtml}
                        </td>
                    </tr>
                    <!-- Footer -->
                    <tr style=""background-color: #f1f5f9; text-align: center; border-top: 1px solid #e2e8f0;"">
                        <td style=""padding: 20px 30px; font-size: 12px; color: #64748b;"">
                            <p style=""margin: 0 0 8px 0;"">This is an automated message from PDFEngine. Please do not reply directly.</p>
                            <p style=""margin: 0;"">&copy; {DateTime.UtcNow.Year} PDFEngine Inc. All rights reserved.</p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    public async Task SendVerificationEmailAsync(User user, string token)
    {
        var verificationUrl = $"{ClientUrl}/auth/verify-email?token={token}";
        var content = $@"
            <p>Welcome to PDFEngine! To complete your sign-up process, please verify your email address by clicking the button below.</p>
            <p>If the button doesn't work, you can copy and paste the following link into your browser:</p>
            <p style=""word-break: break-all; font-family: monospace; background-color: #f1f5f9; padding: 10px; border-radius: 4px; font-size: 13px;"">{verificationUrl}</p>
            <p>This verification link will expire in 24 hours.</p>";

        var body = GetHtmlTemplate("Verify your email address", content, "Verify Email", verificationUrl);
        await LogAndSendEmailAsync(user.TenantId, user.Email, "Verify your PDFEngine email address", body, "Verification");
    }

    public async Task SendWelcomeEmailAsync(User user)
    {
        var dashboardUrl = $"{ClientUrl}/dashboard";
        var content = $@"
            <p>We're thrilled to have you with us. PDFEngine is built for modern developers who need lightning-fast, production-grade PDF generation.</p>
            <p>Here's what you can do next:</p>
            <ul style=""padding-left: 20px;"">
                <li style=""margin-bottom: 8px;"">Generate your first API keys inside the dashboard.</li>
                <li style=""margin-bottom: 8px;"">Configure your storage provider (or use our default S3 bucket).</li>
                <li style=""margin-bottom: 8px;"">Integrate our SDKs into your app.</li>
            </ul>
            <p>If you have any questions or need technical support, don't hesitate to reach out to our team.</p>";

        var body = GetHtmlTemplate("Welcome to PDFEngine!", content, "Go to Dashboard", dashboardUrl);
        await LogAndSendEmailAsync(user.TenantId, user.Email, "Welcome to PDFEngine", body, "Welcome");
    }

    public async Task SendApiKeyCreatedEmailAsync(User user, string keyName)
    {
        var settingsUrl = $"{ClientUrl}/dashboard/settings";
        var content = $@"
            <p>A new API Key named <strong>{keyName}</strong> has been generated for your PDFEngine account.</p>
            <p style=""background-color: #fffbeb; border-left: 4px solid #f59e0b; padding: 12px; border-radius: 4px; color: #78350f;"">
                <strong>Warning:</strong> If you did not initiate this key creation, someone else may have gained access to your account. Please log in immediately and revoke this API key.
            </p>";

        var body = GetHtmlTemplate("New API Key Generated", content, "Manage API Keys", settingsUrl);
        await LogAndSendEmailAsync(user.TenantId, user.Email, "Security Alert: New API Key Created", body, "SecurityAlert");
    }

    public async Task SendTeamInvitationEmailAsync(string email, string tenantName, string token)
    {
        var inviteUrl = $"{ClientUrl}/auth/invite?token={token}";
        var content = $@"
            <p>You have been invited to join the <strong>{tenantName}</strong> team on PDFEngine.</p>
            <p>As part of this team, you will be able to collaborate on template designs, manage rendering pipelines, inspect HAR logs, and review usage metrics.</p>
            <p>Click the button below to accept the invitation and set up your account profile:</p>";

        var body = GetHtmlTemplate($"Join {tenantName} on PDFEngine", content, "Accept Invitation", inviteUrl);
        // Note: For invitations, we might not have a user yet, but we have a TenantId associated with the invite.
        // We will retrieve the TenantId from the invite context before calling this.
        // Let's ensure the caller passes a valid TenantId or logs it accordingly.
        // Since the interface signature doesn't pass TenantId, let's log the invitation using a dummy or default tenant ID if needed, 
        // but wait: SendTeamInvitationEmailAsync has tenantName and token, let's look at the database config or how invitations are stored.
        // If we need TenantId, we'll try to find the invitation in the database to get its TenantId.
        // Let's resolve TenantId inside the method!
        Guid tenantId = Guid.Empty;
        try
        {
            var invitation = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                _dbContext.Invitations, i => i.Token == token).Result;
            if (invitation != null)
            {
                tenantId = invitation.TenantId;
            }
        }
        catch
        {
            // Fallback
        }

        await LogAndSendEmailAsync(tenantId, email, $"Invitation to join {tenantName} on PDFEngine", body, "Invitation");
    }

    public async Task SendPasswordResetEmailAsync(User user, string token)
    {
        var resetUrl = $"{ClientUrl}/auth/reset-password?token={token}";
        var content = $@"
            <p>We received a request to reset the password for your PDFEngine account.</p>
            <p>Click the button below to choose a new password:</p>
            <p>If you did not request this change, you can safely ignore this email. Your password will remain unchanged.</p>
            <p>This link will expire in 1 hour.</p>";

        var body = GetHtmlTemplate("Reset your password", content, "Reset Password", resetUrl);
        await LogAndSendEmailAsync(user.TenantId, user.Email, "Reset your PDFEngine Password", body, "PasswordReset");
    }

    public async Task SendUsageThresholdAlertAsync(Tenant tenant, string userEmail, int percentage)
    {
        var billingUrl = $"{ClientUrl}/dashboard/billing";
        var content = $@"
            <p>Your PDFEngine workspace has reached <strong>{percentage}%</strong> of its monthly rendering limit.</p>
            <p>To avoid service interruptions or automatic rate limiting, consider upgrading your subscription plan.</p>";

        var body = GetHtmlTemplate($"Usage Warning: {percentage}% Limit Reached", content, "Manage Plan", billingUrl);
        await LogAndSendEmailAsync(tenant.Id, userEmail, $"Usage Alert: {percentage}% of Monthly Render Limit Reached", body, "QuotaLimitWarning");
    }

    public async Task SendWebhookFailureAlertAsync(Tenant tenant, string userEmail, string endpointUrl, string error)
    {
        var webhooksUrl = $"{ClientUrl}/dashboard/webhooks";
        var content = $@"
            <p>We detected that a webhook payload failed to deliver to your endpoint: <strong>{endpointUrl}</strong>.</p>
            <p><strong>Error Details:</strong></p>
            <pre style=""background-color: #f1f5f9; padding: 12px; border-radius: 4px; font-family: monospace; font-size: 13px; overflow-x: auto;"">{error}</pre>
            <p>Please inspect the Webhook Logs in your dashboard to retry failed webhooks or debug the response payloads.</p>";

        var body = GetHtmlTemplate("Webhook Delivery Failure", content, "Inspect Webhooks", webhooksUrl);
        await LogAndSendEmailAsync(tenant.Id, userEmail, "Alert: Webhook Delivery Failure", body, "WebhookFailureAlert");
    }

    public async Task SendRenderFailureAlertAsync(Tenant tenant, string userEmail, string jobId, string error)
    {
        var logsUrl = $"{ClientUrl}/dashboard/logs";
        var content = $@"
            <p>A high-priority PDF rendering job (ID: <strong>{jobId}</strong>) failed in your workspace.</p>
            <p><strong>Error Details:</strong></p>
            <pre style=""background-color: #f1f5f9; padding: 12px; border-radius: 4px; font-family: monospace; font-size: 13px; overflow-x: auto;"">{error}</pre>
            <p>You can replay this rendering task or inspect stylesheet errors inside the Render Inspector.</p>";

        var body = GetHtmlTemplate("PDF Rendering Job Failed", content, "Inspect Render Logs", logsUrl);
        await LogAndSendEmailAsync(tenant.Id, userEmail, "Alert: PDF Rendering Job Failed", body, "RenderFailureAlert");
    }
}
