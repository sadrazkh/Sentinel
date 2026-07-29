namespace Sentinel.Domain.Common;

/// <summary>
/// Primary keys are UUID v7: still unguessable from the outside, but time-ordered,
/// so clustered/B-tree index inserts stay sequential instead of scattering pages.
/// </summary>
public static class SequentialGuid
{
    public static Guid New() => Guid.CreateVersion7();

    public static Guid New(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
