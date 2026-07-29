using System.Text.Json;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Serialises the data a Vue island reads from its <c>data-</c> attribute.
/// <para>
/// The default <c>JavaScriptEncoder</c> is kept deliberately: it escapes <c>&lt;</c>,
/// <c>&gt;</c> and <c>&amp;</c> to <c>\uXXXX</c>. Razor already encodes the attribute value, so
/// this is the second of two independent layers, and the cost — non-ASCII text is escaped too,
/// which compresses away — is not worth trading for a relaxed encoder in a payload that carries
/// user-supplied application names.
/// </para>
/// </summary>
public static class IslandPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
