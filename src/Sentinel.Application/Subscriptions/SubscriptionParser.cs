using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Sentinel.Application.Subscriptions;

/// <summary>
/// Turns a subscription payload into structured entries.
/// <para>
/// Everything here treats its input as hostile. The body comes from a third-party server the
/// member nominated, so a malformed line is skipped rather than allowed to throw, and no field
/// is trusted for length or content — the remark in particular is free text that ends up on a
/// page and in a Telegram message.
/// </para>
/// </summary>
public static class SubscriptionParser
{
    /// <summary>Beyond this many entries the rest is ignored, so one payload cannot fill a page.</summary>
    public const int MaxConfigs = 500;

    private const int MaxRemarkLength = 120;

    public static IReadOnlyList<ProxyConfig> ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var text = Decode(body);
        var configs = new List<ProxyConfig>();

        foreach (var rawLine in text.Split('\n'))
        {
            if (configs.Count >= MaxConfigs)
            {
                break;
            }

            var line = rawLine.Trim().Trim('\r');

            // Blank lines and the comment styles panels sprinkle through these files.
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseLine(line, out var config))
            {
                configs.Add(config);
            }
        }

        return configs;
    }

    /// <summary>
    /// Most panels return the whole body base64-encoded; some return plain text. Detected
    /// rather than configured, because the same provider can switch on the client's user agent.
    /// </summary>
    internal static string Decode(string body)
    {
        var trimmed = body.Trim();

        if (!LooksLikeBase64(trimmed))
        {
            return trimmed;
        }

        try
        {
            // Panels emit both the standard and URL-safe alphabets, and routinely drop padding.
            var normalized = trimmed
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace('-', '+')
                .Replace('_', '/');

            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));

            // Only accept the decode if it actually produced configuration lines. Otherwise a
            // plain-text body that happened to look like base64 would be turned into noise.
            return decoded.Contains("://", StringComparison.Ordinal) ? decoded : trimmed;
        }
        catch (FormatException)
        {
            return trimmed;
        }
    }

    private static bool LooksLikeBase64(string value)
    {
        if (value.Length < 16 || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            var isBase64Character = char.IsAsciiLetterOrDigit(character)
                                    || character is '+' or '/' or '=' or '-' or '_'
                                    || char.IsWhiteSpace(character);

            if (!isBase64Character)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryParseLine(string line, out ProxyConfig config)
    {
        config = default!;

        var separator = line.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var scheme = line[..separator].ToLowerInvariant();

        var protocol = scheme switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "ss" => ProxyProtocol.Shadowsocks,
            "hysteria2" or "hy2" => ProxyProtocol.Hysteria2,
            "tuic" => ProxyProtocol.Tuic,
            _ => ProxyProtocol.Unknown,
        };

        // An unrecognised scheme is skipped rather than shown as a mystery card the member
        // cannot use.
        if (protocol == ProxyProtocol.Unknown)
        {
            return false;
        }

        config = protocol == ProxyProtocol.Vmess
            ? ParseVmess(line)
            : ParseUriStyle(protocol, line);

        return true;
    }

    /// <summary>
    /// vless, trojan, ss, hysteria2 and tuic all use <c>scheme://credential@host:port?params#remark</c>.
    /// </summary>
    private static ProxyConfig ParseUriStyle(ProxyProtocol protocol, string line)
    {
        var remark = ExtractRemark(line);
        var withoutFragment = StripFragment(line);

        string? host = null;
        int? port = null;
        string? security = null;
        string? network = null;
        string? sni = null;

        if (Uri.TryCreate(withoutFragment, UriKind.Absolute, out var uri))
        {
            host = string.IsNullOrEmpty(uri.Host) ? null : uri.Host;
            port = uri.Port > 0 ? uri.Port : null;

            var query = HttpUtility.ParseQueryString(uri.Query);
            security = Clean(query["security"]);
            network = Clean(query["type"]) ?? Clean(query["net"]);
            sni = Clean(query["sni"]) ?? Clean(query["host"]);
        }

        return new ProxyConfig(protocol, remark, host, port, security, network, sni, line);
    }

    /// <summary>
    /// vmess is the odd one: the part after the scheme is base64-encoded JSON rather than a URI.
    /// </summary>
    private static ProxyConfig ParseVmess(string line)
    {
        var payload = line["vmess://".Length..].Trim();
        var fallback = new ProxyConfig(ProxyProtocol.Vmess, "vmess", null, null, null, null, null, line);

        try
        {
            var normalized = payload.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            return new ProxyConfig(
                ProxyProtocol.Vmess,
                Truncate(ReadString(root, "ps") ?? "vmess"),
                ReadString(root, "add"),
                ReadPort(root),
                Clean(ReadString(root, "tls")),
                Clean(ReadString(root, "net")),
                Clean(ReadString(root, "sni")) ?? Clean(ReadString(root, "host")),
                line);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            // Malformed entries are shown with what little is known rather than dropping the
            // whole subscription because one line was bad.
            return fallback;
        }
    }

    private static string ExtractRemark(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);

        if (hash < 0 || hash == line.Length - 1)
        {
            return string.Empty;
        }

        var raw = line[(hash + 1)..];

        // Remarks routinely carry Persian text and emoji, percent-encoded by the panel.
        var decoded = Uri.UnescapeDataString(raw);

        return Truncate(decoded);
    }

    private static string StripFragment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadPort(JsonElement element)
    {
        if (!element.TryGetProperty("port", out var value))
        {
            return null;
        }

        // Panels write the port as a number or as a string, interchangeably.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim());

    private static string Truncate(string value)
    {
        var collapsed = value.Trim();

        // Control characters in a remark would break out of whatever it is rendered into.
        collapsed = new string(collapsed.Where(c => !char.IsControl(c)).ToArray());

        return collapsed.Length <= MaxRemarkLength ? collapsed : collapsed[..MaxRemarkLength];
    }
}
