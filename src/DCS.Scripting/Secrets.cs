using System.Security.Cryptography;
using System.Text;

namespace DCS.Scripting;

/// <summary>
/// The MCP API key and DCS link secret: generation and checking. Storage is the app's job
/// (Windows Credential Locker) — library code never persists or logs a secret.
/// </summary>
public static class Secrets
{
    /// <summary>32 random bytes, base64url without padding (43 chars, [A-Za-z0-9_-]) — safe to
    /// embed in an HTTP header, a command line and a Lua string literal.</summary>
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True if the value contains only base64url characters (what <see cref="NewSecret"/> produces).</summary>
    public static bool IsWellFormed(string? secret) =>
        !string.IsNullOrEmpty(secret) && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Checks an Authorization header against the expected key in constant time, so response timing
    /// can't be used to guess the key byte by byte.
    /// </summary>
    public static bool BearerMatches(string? authorizationHeader, string expectedKey)
    {
        const string scheme = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        byte[] presented = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        byte[] expected = Encoding.UTF8.GetBytes(expectedKey);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
