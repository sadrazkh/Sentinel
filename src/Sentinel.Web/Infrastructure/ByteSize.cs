using System.Globalization;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Formats a byte count for a label.
/// <para>
/// Binary units, because that is what an operating system reports for a downloaded file: telling
/// a member "100 MB" for something their file manager calls "95.4 MiB" invites a support ticket
/// about a corrupt download.
/// </para>
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Describe(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // No decimal for bytes and KiB, one from MiB up: "1.4 GiB" is useful, "1434.2 KiB" is not.
        var format = unit <= 1 ? "0" : "0.#";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}");
    }
}
