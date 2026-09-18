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

/// <summary>Identity account emails (confirmation, password reset) over SMTP.</summary>
public sealed partial class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender<ApplicationUser>
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

    private async Task SendAsync(string to, string subject, string intro, string link)
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

        var encodedLink = WebUtility.HtmlEncode(link);
        message.Body = new BodyBuilder
        {
            TextBody = $"{intro}\n\n{link}\n",
            HtmlBody = $"<p>{WebUtility.HtmlEncode(intro)}</p><p><a href=\"{encodedLink}\">{encodedLink}</a></p>",
        }.ToMessageBody();

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
