using System;

namespace PdfEngine.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }

    // Onboarding Questionnaire Fields
    public string FullName { get; set; } = string.Empty;
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public string DiscoverySource { get; set; } = string.Empty;
    
    // SaaS Production Authentication & Onboarding
    public bool IsEmailVerified { get; set; } = false;
    public string? EmailVerificationToken { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetExpires { get; set; }
    public bool OnboardingCompleted { get; set; } = false;
    public int OnboardingStep { get; set; } = 1;
    public string UseCase { get; set; } = string.Empty;
    public string TeamSize { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;

    // Navigation property
    public Tenant? Tenant { get; set; }
}
