using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pidge.Data;
using Pidge.Models;

namespace Pidge.Services;

/// <summary>
/// "Sign in with Microsoft" for Outlook.com, Hotmail, Live and Microsoft 365 mailboxes. Microsoft no longer accepts
/// passwords or app passwords over IMAP, so Pidge uses OAuth 2.0 (authorisation code + PKCE, browser + loopback redirect).
/// The refresh token is stored DPAPI-encrypted in place of a password.
/// </summary>
public static class MicrosoftAuth
{
    /// <summary>
    /// Application (client) ID of the Pidge app registration in Microsoft Entra ID.
    /// Can be overridden in Settings (useful for organisations that register their own).
    /// </summary>
    public const string BuiltInClientId = "";

    public static string ClientId =>
        !string.IsNullOrWhiteSpace(AppSettings.Current.MicrosoftClientId) ? AppSettings.Current.MicrosoftClientId.Trim() : BuiltInClientId;

    public static bool IsConfigured => ClientId.Length > 0;

    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    private const string Scopes = "offline_access openid email https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly ConcurrentDictionary<long, (string token, DateTime expires)> AccessTokens = new();
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public record SignInResult(string Email, string RefreshToken, string AccessToken);

    /// <summary>Opens the browser for the user to sign in and returns their tokens.</summary>
    public static async Task<SignInResult> SignInAsync(string? loginHint, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Microsoft sign-in isn't set up in this build yet. Add a client ID in Settings → Accounts.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var port = FreePort();
        var redirect = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var url = $"{Authority}/authorize?client_id={Uri.EscapeDataString(ClientId)}&response_type=code&response_mode=query" +
                  $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}&prompt=select_account" +
                  (string.IsNullOrWhiteSpace(loginHint) ? "" : $"&login_hint={Uri.EscapeDataString(loginHint)}");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        var error = query["error_description"] ?? query["error"];
        await RespondAsync(context, error == null);

        if (error != null) throw new InvalidOperationException("Microsoft sign-in failed: " + error);
        if (query["state"] != state || string.IsNullOrEmpty(query["code"])) throw new InvalidOperationException("Microsoft sign-in was interrupted.");

        var tokens = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"]!,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
            ["scope"] = Scopes
        }, ct);

        var email = EmailFromIdToken(tokens.idToken) ?? loginHint ?? "";
        return new SignInResult(email, tokens.refresh ?? "", tokens.access);
    }

    /// <summary>Returns a valid access token for the account, refreshing (and storing a rotated refresh token) as needed.</summary>
    public static async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct = default)
    {
        if (AccessTokens.TryGetValue(account.Id, out var cached) && cached.expires > DateTime.UtcNow.AddMinutes(2)) return cached.token;

        await RefreshLock.WaitAsync(ct);
        try
        {
            if (AccessTokens.TryGetValue(account.Id, out cached) && cached.expires > DateTime.UtcNow.AddMinutes(2)) return cached.token;
            var refresh = Secrets.Unprotect(account.ProtectedPassword);
            if (string.IsNullOrEmpty(refresh)) throw new MailKit.Security.AuthenticationException("Sign in to Microsoft again from Edit account.");

            var tokens = await TokenRequestAsync(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["scope"] = Scopes
            }, ct);

            if (!string.IsNullOrEmpty(tokens.refresh) && tokens.refresh != refresh)
            {
                account.ProtectedPassword = Secrets.Protect(tokens.refresh);
                if (account.Id > 0)
                {
                    var suppress = MailStore.SuppressConfigEvents;
                    MailStore.SuppressConfigEvents = true; // token rotation isn't a settings change worth syncing
                    try { MailStore.UpdateAccount(account); } finally { MailStore.SuppressConfigEvents = suppress; }
                }
            }
            AccessTokens[account.Id] = (tokens.access, DateTime.UtcNow.AddSeconds(tokens.expiresIn));
            return tokens.access;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public static void Forget(long accountId) => AccessTokens.TryRemove(accountId, out _);

    private static async Task<(string access, string? refresh, string? idToken, int expiresIn)> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!resp.IsSuccessStatusCode)
        {
            var message = root.TryGetProperty("error_description", out var d) ? d.GetString() : resp.ReasonPhrase;
            throw new MailKit.Security.AuthenticationException("Microsoft refused the sign-in: " + message?.Split('\n')[0]);
        }
        return (root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.TryGetProperty("id_token", out var i) ? i.GetString() : null,
            root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600);
    }

    private static string? EmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        foreach (var claim in new[] { "email", "preferred_username", "upn" })
            if (doc.RootElement.TryGetProperty(claim, out var v) && v.GetString() is { } s && s.Contains('@')) return s;
        return null;
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok)
    {
        var html = $"""
            <html><body style="background:#0E1016;color:#E8EAF0;font-family:Segoe UI,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0">
            <div style="text-align:center"><h2>{(ok ? "You're signed in" : "Sign-in didn't complete")}</h2>
            <p style="color:#A0A8BA">You can close this tab and go back to Pidge.</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
