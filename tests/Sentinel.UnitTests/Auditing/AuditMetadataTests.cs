using System.Text.Json;
using Sentinel.Application.Auditing;

namespace Sentinel.UnitTests.Auditing;

public sealed class AuditMetadataTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("user.password.new")]
    [InlineData("apiKey")]
    [InlineData("refreshToken")]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("Authorization")]
    [InlineData("otp")]
    public void Keys_that_look_like_secrets_are_rejected(string key)
    {
        var metadata = AuditMetadata.Create();

        var exception = Assert.Throws<ArgumentException>(() => metadata.Set(key, "value"));
        Assert.Contains("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetChange_also_screens_the_field_name()
    {
        var metadata = AuditMetadata.Create();

        Assert.Throws<ArgumentException>(() => metadata.SetChange("password", "a", "b"));
    }

    [Fact]
    public void Ordinary_keys_are_accepted_and_serialised()
    {
        var json = AuditMetadata.Create()
            .Set("reason", "Expired")
            .Set("sessionsRevoked", 3)
            .ToJson();

        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;
        Assert.Equal("Expired", parsed["reason"]);
        Assert.Equal("3", parsed["sessionsRevoked"]);
    }

    [Fact]
    public void Empty_metadata_serialises_to_null_rather_than_an_empty_object()
    {
        Assert.Null(AuditMetadata.Create().ToJson());
    }

    [Fact]
    public void Long_values_are_truncated_so_one_entry_cannot_overflow_the_column()
    {
        var json = AuditMetadata.Create()
            .Set("userAgent", new string('x', AuditMetadata.MaxValueLength * 3))
            .ToJson();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;

        // Truncated to the cap plus the single-character ellipsis marker.
        Assert.Equal(AuditMetadata.MaxValueLength + 1, parsed["userAgent"].Length);
    }

    [Fact]
    public void The_number_of_entries_is_capped()
    {
        var metadata = AuditMetadata.Create();

        for (var i = 0; i < AuditMetadata.MaxEntries; i++)
        {
            metadata.Set($"field{i}", i);
        }

        Assert.Throws<InvalidOperationException>(() => metadata.Set("oneTooMany", 1));
    }

    [Fact]
    public void Overwriting_an_existing_key_does_not_count_against_the_cap()
    {
        var metadata = AuditMetadata.Create();

        for (var i = 0; i < AuditMetadata.MaxEntries; i++)
        {
            metadata.Set($"field{i}", i);
        }

        metadata.Set("field0", "replaced");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(metadata.ToJson()!)!;
        Assert.Equal("replaced", parsed["field0"]);
    }

    [Fact]
    public void Boolean_and_date_values_use_a_stable_invariant_form()
    {
        var timestamp = new DateTimeOffset(2026, 7, 29, 9, 30, 0, TimeSpan.FromHours(3));

        var json = AuditMetadata.Create()
            .Set("enabled", true)
            .Set("expiresAt", timestamp)
            .ToJson();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;

        Assert.Equal("true", parsed["enabled"]);

        // Normalised to UTC so entries written from different offsets stay comparable.
        Assert.StartsWith("2026-07-29T06:30:00", parsed["expiresAt"], StringComparison.Ordinal);
    }

    [Fact]
    public void Null_values_are_kept_so_a_change_to_empty_is_still_recorded()
    {
        var json = AuditMetadata.Create().SetChange("notes", "old", null).ToJson();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(json!)!;

        Assert.Equal("old", parsed["notes.from"]);
        Assert.Null(parsed["notes.to"]);
    }
}
