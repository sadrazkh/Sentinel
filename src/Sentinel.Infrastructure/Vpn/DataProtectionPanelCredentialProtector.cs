using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Sentinel.Vpn.Panel;

namespace Sentinel.Infrastructure.Vpn;

/// <summary>
/// Protects panel API tokens with the application's data-protection key ring.
/// <para>
/// The same key ring that protects authentication cookies, under its own purpose string so a
/// value from one context can never be decrypted as the other. That means the key ring is now
/// load-bearing for stored credentials as well as for sessions: it has to be persisted and
/// backed up, which is what <c>DataProtection:KeyRingPath</c> is for. Without it, a restart in a
/// container generates new keys and every stored token becomes unreadable.
/// </para>
/// </summary>
public sealed class DataProtectionPanelCredentialProtector : IPanelCredentialProtector
{
    /// <summary>
    /// Changing this string makes every previously stored token unreadable. It is versioned so a
    /// future migration to a different scheme can be deliberate rather than accidental.
    /// </summary>
    private const string Purpose = "Sentinel.Vpn.PanelApiToken.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionPanelCredentialProtector> _logger;

    public DataProtectionPanelCredentialProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionPanelCredentialProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        return _protector.Protect(plaintext);
    }

    public string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex)
        {
            // A rotated key ring, or a database restored without its keys. Reported rather than
            // thrown so a background sweep marks the server unreachable and an operator is asked
            // to re-enter the token — a crash loop would take the whole sweep down with it.
            //
            // The ciphertext is deliberately not logged: it is still a credential.
            _logger.LogError(
                ex,
                "A stored panel token could not be decrypted. The data-protection key ring has "
                + "probably changed. Re-enter the token for the affected server.");

            return null;
        }
    }
}
