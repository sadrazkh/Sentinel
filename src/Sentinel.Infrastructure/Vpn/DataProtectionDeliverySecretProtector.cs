using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Sentinel.Vpn.Delivery;

namespace Sentinel.Infrastructure.Vpn;

/// <summary>
/// Seals delivery tokens with the application's data-protection key ring.
/// <para>
/// Its own purpose string, separate from the panel-credential protector's: a sealed delivery token
/// must never open as a panel API token or the other way round, and the two have very different
/// blast radii — one is a member's configurations, the other is full control of a server.
/// </para>
/// <para>
/// Losing the key ring costs every member their current subscription URL and nothing more. They are
/// re-issued by rotating, which the member can do themselves, so this failure is recoverable without
/// an operator — unlike a lost panel token, which needs re-entering by hand.
/// </para>
/// </summary>
public sealed class DataProtectionDeliverySecretProtector : IDeliverySecretProtector
{
    private const string Purpose = "Sentinel.Vpn.DeliveryToken.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionDeliverySecretProtector> _logger;

    public DataProtectionDeliverySecretProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionDeliverySecretProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Seal(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return _protector.Protect(token);
    }

    public string? Open(string? sealedValue)
    {
        if (string.IsNullOrWhiteSpace(sealedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(sealedValue);
        }
        catch (Exception ex)
        {
            // Reported, not thrown: this runs while rendering a member's own page, and a key ring
            // that has moved on should cost them a "regenerate your link" prompt rather than a 500.
            //
            // The ciphertext is deliberately absent from the message — it is still the credential.
            _logger.LogWarning(
                ex,
                "A sealed delivery token could not be opened. The data-protection key ring has "
                + "probably changed; the member can issue a new link.");

            return null;
        }
    }
}
