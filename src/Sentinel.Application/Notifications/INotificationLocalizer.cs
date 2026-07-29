namespace Sentinel.Application.Notifications;

/// <summary>
/// Resolves notification text in a specific culture rather than the ambient one.
/// <para>
/// A background job has no request, and therefore no request culture — but a notification is
/// addressed to one person whose language the portal already knows. Passing the culture in
/// explicitly is what lets a scheduled sweep write each member's message in their own language
/// instead of whatever the server happened to default to.
/// </para>
/// </summary>
public interface INotificationLocalizer
{
    /// <summary>
    /// Returns the text for <paramref name="key"/> in <paramref name="culture"/>, formatted with
    /// <paramref name="arguments"/>. Falls back to the default language, and finally to the key
    /// itself, rather than throwing — a missing translation must not stop a warning going out.
    /// </summary>
    string Get(string key, string? culture, params object?[] arguments);
}
