using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Binds <see cref="DateTime"/> values from form fields using the invariant calendar.
/// <para>
/// MVC's form value provider parses with <see cref="CultureInfo.CurrentCulture"/>. Under
/// <c>fa-IR</c> that culture uses the Persian calendar, so the ISO string an
/// <c>&lt;input type="date"&gt;</c> always submits — "2026-07-29" — is read as a Persian date
/// and stored as Gregorian year 2647. The value looks plausible in the form and is wrong by
/// six centuries in the database.
/// </para>
/// <para>
/// HTML date and datetime-local inputs are defined to submit ISO-8601 regardless of the
/// user's locale, so parsing them with the invariant culture is not a workaround — it is the
/// correct reading of the wire format. Display formatting still follows the user's culture.
/// </para>
/// </summary>
public sealed class Iso8601DateModelBinder : IModelBinder
{
    /// <summary>The shapes an HTML date, datetime-local or month input can submit.</summary>
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM",
    ];

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);

        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);

        var raw = value.FirstValue;

        if (string.IsNullOrWhiteSpace(raw))
        {
            // An empty optional date is absence, not a parse failure.
            var underlying = Nullable.GetUnderlyingType(bindingContext.ModelType);

            if (underlying is not null)
            {
                bindingContext.Result = ModelBindingResult.Success(null);
            }

            return Task.CompletedTask;
        }

        if (DateTime.TryParseExact(
                raw.Trim(),
                AcceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(
            bindingContext.ModelName, "validation.date");

        return Task.CompletedTask;
    }
}

public sealed class Iso8601DateModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.UnderlyingOrModelType;

        return modelType == typeof(DateTime) ? new Iso8601DateModelBinder() : null;
    }
}
