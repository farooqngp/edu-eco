using System.Net;
using System.Net.Sockets;
using EduEco.Application.Invites;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EduEco.Infrastructure.Email;

/// <summary>
/// SMTP settings for resource-server outbound mail. Deliberately a separate class from
/// <c>EduEco.Identity.Email.SmtpOptions</c> bound to the same <c>Email:Smtp</c> keys: the two hosts are separate
/// processes, so nothing can be shared in-process, and hoisting Identity's shipped options class would disturb its
/// live confirmation/reset email path for no functional gain.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    /// <summary>Empty host disables delivery (the message is logged and dropped).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary><c>StartTls</c> (default), <c>SslOnConnect</c>, or <c>None</c> (local Mailpit only).</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "no-reply@eduEco.local";

    public string FromName { get; set; } = "EduEco";
}

/// <summary>Emails an issued invite code to the invitee. Never throws — see <see cref="IInviteEmailSender"/>.</summary>
public sealed partial class SmtpInviteEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpInviteEmailSender> logger) : IInviteEmailSender
{
    private const string Subject = "You've been invited to EduEco";

    public async Task SendInviteAsync(string email, string code, string roleName, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            LogDeliveryDisabled(logger);
            return;
        }

        try
        {
            var intro = $"You've been invited to join EduEco as {roleName}. Enter this invite code when you register "
                + $"(valid until {expiresAtUtc.UtcDateTime:u}):";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
            message.To.Add(MailboxAddress.Parse(email));
            message.Subject = Subject;
            message.Body = new BodyBuilder
            {
                TextBody = $"{intro}\n\n{code}\n",
                HtmlBody = $"<p>{WebUtility.HtmlEncode(intro)}</p><p style=\"font-size:1.25em;font-weight:bold\">{WebUtility.HtmlEncode(code)}</p>",
            }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(settings.Host, settings.Port, settings.Security, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(settings.UserName))
            {
                await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            LogSent(logger);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or AuthenticationException or IOException or SocketException)
        {
            LogSendFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invite email delivery disabled (Email:Smtp:Host empty); invite code not emailed")]
    private static partial void LogDeliveryDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invite email sent")]
    private static partial void LogSent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invite email delivery failed; the invite was still issued")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);
}
