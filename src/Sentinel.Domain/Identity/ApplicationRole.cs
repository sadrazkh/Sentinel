using Microsoft.AspNetCore.Identity;

namespace Sentinel.Domain.Identity;

public class ApplicationRole : IdentityRole<Guid>
{
    public const int DescriptionMaxLength = 256;

    public ApplicationRole()
    {
    }

    public ApplicationRole(string name, string description) : base(name)
    {
        Description = description;
    }

    public string? Description { get; set; }
}
