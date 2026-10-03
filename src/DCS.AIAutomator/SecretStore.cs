using System;
using DCS.Scripting;
using Windows.Security.Credentials;

namespace DCS.AIAutomator;

/// <summary>
/// The app's two secrets — the MCP API key (clients send it as a bearer token) and the DCS link
/// secret (baked into the deployed Hooks script) — kept in the Windows Credential Locker, per
/// user. App project only, like <see cref="SettingsService"/>: the libraries receive secrets as
/// parameters and never persist or log them.
/// </summary>
public sealed class SecretStore
{
    public const string McpApiKey = "McpApiKey";
    public const string DcsLinkSecret = "DcsLinkSecret";

    private const string Resource = "DCS.AIAutomator";
    private const int ElementNotFound = unchecked((int)0x80070490); // PasswordVault: no such credential

    private readonly PasswordVault _vault = new();

    /// <summary>The stored secret, generated and stored on first use.</summary>
    public string GetOrCreate(string name)
    {
        try
        {
            PasswordCredential credential = _vault.Retrieve(Resource, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            return Regenerate(name);
        }
    }

    /// <summary>Replaces the secret with a new random one and returns it.</summary>
    public string Regenerate(string name)
    {
        try
        {
            _vault.Remove(_vault.Retrieve(Resource, name));
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            // nothing to replace
        }
        string secret = Secrets.NewSecret();
        _vault.Add(new PasswordCredential(Resource, name, secret));
        return secret;
    }
}
