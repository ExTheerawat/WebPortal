using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SingleSignOn.Models;

namespace SingleSignOn.Services;

/// <summary>
/// Mints the short-lived HS256 JWT that a GatewayRedirect app verifies before signing the
/// user in by e-mail (replaces the forgeable bare {email} query parameter).
/// Spec shared with the destination apps: docs/pms-gateway-token-spec.md — keep in sync.
/// </summary>
public static class GatewayTokenService
{
    // Exact string PMS pins as SSO_TOKEN_ISSUER — the portal's real production origin,
    // no trailing slash. Changing this breaks every {token} app until they update config.
    public const string Issuer = "https://portal.penso.co.th";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

    /// <summary>Create a signed token asserting "this e-mail may enter this app right now".
    /// <paramref name="uid"/> = log_users.user_id, added as an optional "uid" claim — a key
    /// that stays stable when a user's e-mail changes (@p9.co.th ↔ @ccthailand.co.th).</summary>
    public static string Create(AppDefinition app, string email, int? uid = null)
    {
        if (string.IsNullOrWhiteSpace(app.GatewaySecret))
            throw new InvalidOperationException(
                $"App '{app.Key}' uses a {{token}} gateway but Sso:Apps:GatewaySecret is not set.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(app.GatewaySecret.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"GatewaySecret for app '{app.Key}' is not valid standard base64.", ex);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = app.Key,                                   // e.g. "pms"
            ["sub"] = email,                                     // already trimmed + lowercased
            ["iat"] = now,
            ["exp"] = now + (long)Lifetime.TotalSeconds,
            ["jti"] = B64Url(RandomNumberGenerator.GetBytes(16)), // nonce for one-time-use checks
        };
        if (uid is > 0) payload["uid"] = uid.Value;

        var signingInput = B64Url(JsonSerializer.SerializeToUtf8Bytes(header))
            + "." + B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + B64Url(signature);
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
