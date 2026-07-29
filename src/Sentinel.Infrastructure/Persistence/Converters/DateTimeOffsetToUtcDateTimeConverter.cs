using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Sentinel.Infrastructure.Persistence.Converters;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as a UTC <see cref="DateTime"/>.
/// <para>
/// Only applied on SQLite, whose provider cannot translate comparisons or ordering on
/// <see cref="DateTimeOffset"/> — a query such as <c>ExpiresAt &gt; now</c> fails outright.
/// The round trip is lossless here because every instant in this application is written in
/// UTC, so there is no offset to lose.
/// </para>
/// <para>
/// PostgreSQL and SQL Server keep the native type (<c>timestamptz</c> / <c>datetimeoffset</c>)
/// and need no conversion.
/// </para>
/// </summary>
public sealed class DateTimeOffsetToUtcDateTimeConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public DateTimeOffsetToUtcDateTimeConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}
