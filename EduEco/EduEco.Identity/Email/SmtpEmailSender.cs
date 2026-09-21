using System.Net;
using EduEco.Core.Identity;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EduEco.Identity.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    /// <summary>Empty host disables delivery (messages are logged without links).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary><c>StartTls</c> (default), <c>SslOnConnect</c>, or <c>None</c> (local Mailpit only).</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "no-reply@eduEco.local";

    public string FromName { get; set; } = "EduEco";
}

/// <summary>Identity account emails (confirmation, password reset, admin provisioning) over SMTP.</summary>
public sealed partial class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    : IEmailSender<ApplicationUser>, IProvisioningEmailSender
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your EduEco email address",
            $"Confirm your email address by opening this link (valid 30 minutes):", confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(email, "Reset your EduEco password",
            "A password reset was requested for your account. If this was you, open this link (valid 30 minutes). "
            + "Otherwise ignore this email; your password stays unchanged.", resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        throw new NotSupportedException("Code-based password reset is not used; links are sent instead.");

    public Task SendTenantAdminInvitationAsync(ApplicationUser user, string email, string tenantName, string setPasswordLink, string loginUrl) =>
        // The set-password link deliberately comes first: it is the only way into the account, and the E2E Mailpit
        // helper extracts the first link in the text body.
        SendBodyAsync(email, $"You are the administrator for {tenantName} on EduEco",
            $"An EduEco account has been created for you as the administrator of {tenantName}.\n\n"
            + $"Set your password using this link (valid 30 minutes):\n{setPasswordLink}\n\n"
            + $"Afterwards, sign in here:\n{loginUrl}\n",
            [("Set your password (valid 30 minutes)", setPasswordLink), ("Sign in", loginUrl)]);

    public Task SendTenantAdminGrantedAsync(ApplicationUser user, string email, string tenantName, string loginUrl) =>
        SendBodyAsync(email, $"You are now an administrator for {tenantName} on EduEco",
            $"Your existing EduEco account has been granted administration of {tenantName}.\n\n"
            + $"Sign in with your usual password here:\n{loginUrl}\n",
            [("Sign in", loginUrl)]);

    private async Task SendBodyAsync(string to, string subject, string textBody, (string Label, string Url)[] links)
    {
        var paragraphs = string.Join(string.Empty, textBody.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p>{WebUtility.HtmlEncode(p.Split('\n')[0])}</p>"));
        var anchors = string.Join(string.Empty, links.Select(l =>
            $"<p><a href=\"{WebUtility.HtmlEncode(l.Url)}\">{WebUtility.HtmlEncode(l.Label)}</a></p>"));

        await DeliverAsync(to, subject, textBody, paragraphs + anchors).ConfigureAwait(false);
    }

    private async Task SendAsync(string to, string subject, string intro, string link)
    {
        var encodedLink = WebUtility.HtmlEncode(link);
        await DeliverAsync(
            to,
            subject,
            $"{intro}\n\n{link}\n",
            $"<p>{WebUtility.HtmlEncode(intro)}</p><p><a href=\"{encodedLink}\">{encodedLink}</a></p>").ConfigureAwait(false);
    }

    /// <summary>Single delivery path for every email; an empty host disables delivery (logged, not sent).</summary>
    private async Task DeliverAsync(string to, string subject, string textBody, string htmlBody)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            LogDeliveryDisabled(logger, subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty).ConfigureAwait(false);
        }

        await client.SendAsync(message).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true).ConfigureAwait(false);
        LogSent(logger, subject);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email delivery disabled (Email:Smtp:Host empty); '{Subject}' not sent")]
    private static partial void LogDeliveryDisabled(ILogger logger, string subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email '{Subject}' sent")]
    private static partial void LogSent(ILogger logger, string subject);
}
