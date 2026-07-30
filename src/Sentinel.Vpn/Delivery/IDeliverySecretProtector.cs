namespace Sentinel.Vpn.Delivery;

/// <summary>
/// Seals a delivery token so its owner can read their own subscription URL again.
/// <para>
/// The hash alone would be enough for the anonymous endpoint, and hashing is what keeps a database
/// leak from handing out working configurations. But a subscription URL is not a password: a member
/// pastes it into every device they own, over months. If the only way to see it again were to issue
/// a new one, adding a second phone would silently break the first — so the portal would be
/// enforcing secrecy by making the feature unusable.
/// </para>
/// <para>
/// So the token is kept twice: hashed, which is what the request path matches against, and sealed
/// with the data-protection key ring, which is what the owner's own page unwraps. The keys live
/// outside the database, so a stolen dump still yields nothing — while an operator with legitimate
/// database access cannot read a member's URL either.
/// </para>
/// </summary>
public interface IDeliverySecretProtector
{
    string Seal(string token);

    /// <summary>Null when the value cannot be opened — a rotated key ring, or a restore without it.</summary>
    string? Open(string? sealedValue);
}
