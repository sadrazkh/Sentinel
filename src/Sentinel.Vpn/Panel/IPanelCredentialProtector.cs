namespace Sentinel.Vpn.Panel;

/// <summary>
/// Encrypts and decrypts a panel's API token.
/// <para>
/// An interface rather than a direct call to the data-protection API so the VPN module does not
/// take a dependency on the web host, and so a test can substitute a trivial implementation
/// without a key ring on disk.
/// </para>
/// <para>
/// <see cref="Unprotect"/> returns <c>null</c> rather than throwing when a value cannot be read.
/// That is the case that matters operationally: rotating the key ring, or restoring a database
/// without its keys, leaves rows that can no longer be decrypted — and the right response is to
/// mark the server unreachable and tell an operator to re-enter the token, not to crash a
/// background sweep.
/// </para>
/// </summary>
public interface IPanelCredentialProtector
{
    string Protect(string plaintext);

    string? Unprotect(string protectedValue);

    /// <summary>
    /// The tail of a token, for an operator to confirm which credential is stored without the
    /// portal ever showing the whole value again.
    /// </summary>
    static string HintFor(string token) =>
        string.IsNullOrEmpty(token) || token.Length <= 4
            ? "····"
            : "····" + token[^4..];
}
