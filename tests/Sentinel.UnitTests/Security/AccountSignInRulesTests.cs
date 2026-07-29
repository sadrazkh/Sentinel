using Sentinel.Application.Security;
using Sentinel.Domain.Identity;

namespace Sentinel.UnitTests.Security;

public sealed class AccountSignInRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationUser User(UserAccountStatus status, DateTimeOffset? suspendedUntil = null) =>
        new() { Status = status, SuspendedUntil = suspendedUntil };

    [Fact]
    public void Active_account_can_sign_in()
    {
        Assert.True(AccountSignInRules.CanSignIn(User(UserAccountStatus.Active), Now));
    }

    [Fact]
    public void Disabled_account_cannot_sign_in()
    {
        Assert.False(AccountSignInRules.CanSignIn(User(UserAccountStatus.Disabled), Now));
    }

    [Fact]
    public void Open_ended_suspension_blocks_sign_in()
    {
        var user = User(UserAccountStatus.Suspended, suspendedUntil: null);

        Assert.False(AccountSignInRules.CanSignIn(user, Now));
    }

    [Fact]
    public void Suspension_still_in_the_future_blocks_sign_in()
    {
        var user = User(UserAccountStatus.Suspended, Now.AddHours(1));

        Assert.False(AccountSignInRules.CanSignIn(user, Now));
    }

    [Fact]
    public void Suspension_lapses_once_its_deadline_passes()
    {
        var user = User(UserAccountStatus.Suspended, Now.AddSeconds(-1));

        Assert.True(AccountSignInRules.CanSignIn(user, Now));
    }

    [Fact]
    public void Suspension_ending_exactly_now_has_lapsed()
    {
        var user = User(UserAccountStatus.Suspended, Now);

        Assert.True(AccountSignInRules.CanSignIn(user, Now));
    }

    [Fact]
    public void Unknown_status_is_refused_rather_than_allowed()
    {
        // Guards the default arm: adding a status without classifying it must not silently
        // grant access.
        var user = User((UserAccountStatus)999);

        Assert.False(AccountSignInRules.CanSignIn(user, Now));
    }
}
