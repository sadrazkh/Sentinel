namespace Sentinel.Domain.Catalog;

public enum ApplicationPublishStatus
{
    /// <summary>Only visible in the admin area. Never listed for members.</summary>
    Draft = 1,

    /// <summary>Listed for members as a teaser, but cannot be launched.</summary>
    ComingSoon = 2,

    /// <summary>Listed and launchable for entitled members.</summary>
    Published = 3,

    /// <summary>Withdrawn. Stays listed for members who already hold it, but cannot be launched.</summary>
    Retired = 4,
}
