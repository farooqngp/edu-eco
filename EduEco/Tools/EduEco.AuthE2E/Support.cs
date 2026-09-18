using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EduEco.Infrastructure.Security;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.AuthE2E;

/// <summary>Endpoints and local secrets of the Docker stack (read from the repository's .env and certs folder).</summary>
internal sealed record Settings(
    Uri Identity,
    Uri Api,
    Uri Bff,
    Uri Mailpit,
    string CertificatesPath,
    string CertificatePassword,
    string DevUserPassword)
{
    public const string TenantAdmin = "tenant.admin@demo.eduEco.local";
    public const string Teacher = "teacher@demo.eduEco.local";

    public static Settings Load(string[] args)
    {
        var root = FindRepositoryRoot();
        var env = ReadEnv(Path.Combine(root, ".env"));

        string Arg(string name, string fallback)
        {
            var index = Array.IndexOf(args, "--" + name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        return new Settings(
            new Uri(Arg("identity", "https://localhost:7013/")),
            new Uri(Arg("api", "https://localhost:7037/")),
            new Uri(Arg("bff", "https://localhost:7100/")),
            new Uri(Arg("mailpit", "http://localhost:8025/")),
            Path.Combine(root, "certs"),
            env.GetValueOrDefault("IDENTITY_CERT_PASSWORD") ?? throw new InvalidOperationException("IDENTITY_CERT_PASSWORD missing in .env"),
            env.GetValueOrDefault("DEV_USER_PASSWORD") ?? throw new InvalidOperationException("DEV_USER_PASSWORD missing in .env"));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Repository root (docker-compose.yml) not found; run from inside the repository.");
    }

    private static Dictionary<string, string> ReadEnv(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{path} not found. Copy .env.example to .env first.");
        }

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('=', StringComparison.Ordinal))
            .Select(line => (Key: line[..line.IndexOf('=', StringComparison.Ordinal)].Trim(), Value: line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim()))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
    }

    public SigningCredentials ClientCredentials(string fileName) =>
        KeyMaterial.ToSigningCredentials(KeyMaterial.LoadPkcs12(Path.Combine(CertificatesPath, fileName), CertificatePassword));
}

internal sealed class CheckFailedException(string message) : Exception(message);

internal static class Expect
{
    public static void That(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    public static T NotNull<T>(T? value, string message)
        where T : class =>
        value ?? throw new CheckFailedException(message);

    public static async Task<JsonElement> StatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        That(response.StatusCode == expected, $"expected {(int)expected} but got {(int)response.StatusCode}: {Trim(body)}");
        return body.Length > 0 && (body[0] == '{' || body[0] == '[') ? JsonDocument.Parse(body).RootElement.Clone() : default;
    }

    public static string Trim(string text) => text.Length <= 240 ? text : text[..240] + "…";
}

/// <summary>A browser-like client: own cookie jar, manual redirects, dev-certificate trust on loopback only.</summary>
internal sealed partial class Browser : IDisposable
{
    private readonly Uri _identity;

    public Browser(Uri identity)
    {
        _identity = identity;
        Cookies = new CookieContainer();
#pragma warning disable CA2000 // Owned by the HttpClient.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = Cookies,
            ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
                errors == SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
        };
#pragma warning restore CA2000
        Http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public HttpClient Http { get; }

    public CookieContainer Cookies { get; }

    public static HttpClient CreateApiClient(Uri baseAddress)
    {
#pragma warning disable CA2000 // Owned by the HttpClient.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
                errors == SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
        };
#pragma warning restore CA2000
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Follows redirects from <paramref name="start"/>, filling the Identity login form when asked, and returns the first
    /// redirect that leaves the Identity origin (the client callback, possibly a custom scheme).
    /// </summary>
    public async Task<Uri> AuthorizeAsync(Uri start, string email, string password)
    {
        var current = start;
        var response = await Http.GetAsync(current).ConfigureAwait(false);
        for (var hop = 0; hop < 15; hop++)
        {
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new CheckFailedException(
                        $"stopped at {current.AbsolutePath} with {(int)response.StatusCode}{ExtractError(body)}");
                }

                var location = new Uri(current, response.Headers.Location!);
                if (!SameOrigin(location, _identity))
                {
                    return location;
                }

                current = location;
                if (location.AbsolutePath.Equals("/Account/SelectTenant", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CheckFailedException("tenant picker shown (user has several tenants); pass a tenant parameter");
                }
            }

            response = current.AbsolutePath.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase)
                ? await PostFormAsync(current, new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password }).ConfigureAwait(false)
                : await Http.GetAsync(current).ConfigureAwait(false);
        }

        throw new CheckFailedException("too many redirects");
    }

    /// <summary>GETs the page for its anti-forgery token, then POSTs the form to the same URL.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(Uri page, IDictionary<string, string> fields)
    {
        var html = await Http.GetStringAsync(page).ConfigureAwait(false);
        var token = AntiforgeryRegex().Match(html);
        Expect.That(token.Success, $"no anti-forgery token on {page.AbsolutePath}");

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) };
        using var content = new FormUrlEncodedContent(form);
        return await Http.PostAsync(page, content).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> GetAsync(Uri uri, bool csrf = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        return await Http.SendAsync(request).ConfigureAwait(false);
    }

    public void Dispose() => Http.Dispose();

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static string ExtractError(string html)
    {
        var match = ErrorRegex().Match(html);
        return match.Success ? ": " + WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : string.Empty;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();

    [GeneratedRegex("<(?:div|p|li)[^>]*class=\"[^\"]*error[^\"]*\"[^>]*>\\s*(?:<ul>\\s*<li>)?([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();
}

internal static class Pkce
{
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }
}

internal static class Query
{
    public static string? Get(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (key == name)
            {
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    public static string Build(Uri baseUri, string path, IEnumerable<KeyValuePair<string, string>> parameters) =>
        new Uri(baseUri, path) + "?" + string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}

/// <summary>Reads messages captured by Mailpit (development SMTP sink).</summary>
internal sealed class Mailbox(Uri mailpit) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = mailpit, Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Waits for the newest message to <paramref name="to"/> whose subject contains <paramref name="subject"/> and returns its first link.</summary>
    public async Task<Uri> WaitForLinkAsync(string to, string subject, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var search = await _http.GetStringAsync(new Uri("api/v1/search?query=" + Uri.EscapeDataString($"to:\"{to}\""), UriKind.Relative)).ConfigureAwait(false);
            using var results = JsonDocument.Parse(search);
            var message = results.RootElement.GetProperty("messages").EnumerateArray()
                .FirstOrDefault(m => m.GetProperty("Subject").GetString()?.Contains(subject, StringComparison.OrdinalIgnoreCase) == true);

            if (message.ValueKind == JsonValueKind.Object)
            {
                var id = message.GetProperty("ID").GetString();
                using var detail = JsonDocument.Parse(await _http.GetStringAsync(new Uri($"api/v1/message/{id}", UriKind.Relative)).ConfigureAwait(false));
                var text = detail.RootElement.GetProperty("Text").GetString() ?? string.Empty;
                var link = Regex.Match(text, @"https://\S+");
                Expect.That(link.Success, $"no link in '{subject}' email");
                return new Uri(link.Value);
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new CheckFailedException($"no '{subject}' email for {to} in Mailpit within {timeout.TotalSeconds:0} s");
    }

    public void Dispose() => _http.Dispose();
}

internal static class Http
{
    public static AuthenticationHeaderValue Scheme(string scheme, string token) => new(scheme, token);

    public static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
