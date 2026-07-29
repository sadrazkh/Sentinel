namespace Sentinel.Domain.Common;

/// <summary>
/// Entities whose create/update timestamps are maintained centrally in
/// <c>SentinelDbContext.SaveChangesAsync</c> from the injected <see cref="TimeProvider"/>.
/// This is why no service calls <c>DateTimeOffset.UtcNow</c> to stamp a row.
/// </summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}
