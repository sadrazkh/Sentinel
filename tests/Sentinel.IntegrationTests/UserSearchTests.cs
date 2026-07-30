using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Users;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Finding a member in the back office.
/// <para>
/// The phone case is the one that was broken, and it was broken invisibly: a number is stored in
/// E.164 ("+989120000001") and nobody types that, so searching for "09120000001" — the form printed
/// on the member's own account page — matched nothing at all.
/// </para>
/// </summary>
public sealed class UserSearchTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public UserSearchTests(SentinelWebApplicationFactory factory) => _factory = factory;

    /// <summary>A member with a known phone number, stored the way the portal stores one.</summary>
    private async Task<Guid> WithPhoneAsync(string userName, string phone)
    {
        var userId = await _factory.CreateMemberAsync(userName);

        await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            var user = await db.Users.FirstAsync(candidate => candidate.Id == userId);

            user.PhoneNumber = phone;
            user.NormalizedPhoneNumber =
                Sentinel.Application.Identity.PhoneNumberNormalizer.Normalize(phone);

            await db.SaveChangesAsync();
        });

        return userId;
    }

    private Task<IReadOnlyList<string>> SearchAsync(string term) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IUserAdminQuery>()
                .SearchAsync(new UserListRequest(Search: term).Normalized());

            return (IReadOnlyList<string>)result.Items.Select(item => item.UserName).ToList();
        });

    [Theory]
    // The national form, which is what is printed on the member's own page and what an operator
    // copies out of a support message.
    [InlineData("09121110001")]
    // Part of it — somebody typing what they remember.
    [InlineData("0912111")]
    [InlineData("1110001")]
    // Already international, with and without the plus.
    [InlineData("+989121110001")]
    [InlineData("989121110001")]
    // Persian digits, which is what a Persian keyboard produces.
    [InlineData("۰۹۱۲۱۱۱۰۰۰۱")]
    // Pasted from a contacts app.
    [InlineData("0912 111 0001")]
    public async Task A_member_is_found_by_the_phone_number_as_anyone_would_type_it(string term)
    {
        await WithPhoneAsync("search-phone-target", "+989121110001");

        var found = await SearchAsync(term);

        Assert.Contains("search-phone-target", found);
    }

    [Fact]
    public async Task A_different_number_is_not_matched()
    {
        // The clause has to be narrow enough to be useful. A search that returned everyone with a
        // phone would be no better than the one that returned nobody.
        await WithPhoneAsync("search-phone-mine", "+989121110002");
        await WithPhoneAsync("search-phone-theirs", "+989335550000");

        var found = await SearchAsync("09121110002");

        Assert.Contains("search-phone-mine", found);
        Assert.DoesNotContain("search-phone-theirs", found);
    }

    [Fact]
    public async Task Searching_by_name_and_username_still_works()
    {
        // The phone clause is added beside the others, not instead of them.
        await _factory.CreateMemberAsync("search-by-name");

        Assert.Contains("search-by-name", await SearchAsync("search-by-name"));
    }

    [Fact]
    public async Task A_term_with_no_digits_does_not_match_every_member_with_a_phone()
    {
        // The failure mode of a careless fix: reduce "Sadra" to an empty fragment, LIKE '%%', and
        // every member with a number comes back.
        await WithPhoneAsync("search-nodigits-has-phone", "+989121110003");

        var found = await SearchAsync("zzz-no-such-member-anywhere");

        Assert.Empty(found);
    }
}
