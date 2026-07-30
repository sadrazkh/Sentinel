using Sentinel.Vpn.Domain;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// The migration record's own logic — which steps a worker may pick up, and when the customer is
/// live on two panels at once.
/// <para>
/// Worth testing apart from the saga because both are read in more than one place, and both are the
/// kind of predicate that quietly becomes wrong when a step is added to the enum.
/// </para>
/// </summary>
public sealed class ServiceMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static ServiceMigration At(MigrationStep step) => new()
    {
        Id = Guid.NewGuid(),
        ServiceId = Guid.NewGuid(),
        SourceServerId = Guid.NewGuid(),
        DestinationServerId = Guid.NewGuid(),
        Step = step,
    };

    [Theory]
    [InlineData(MigrationStep.Planned)]
    [InlineData(MigrationStep.Creating)]
    [InlineData(MigrationStep.Verifying)]
    [InlineData(MigrationStep.Detaching)]
    public void A_step_still_in_progress_is_runnable(MigrationStep step) =>
        Assert.True(At(step).IsRunnableAt(Now));

    [Theory]
    [InlineData(MigrationStep.Completed)]
    [InlineData(MigrationStep.Abandoned)]
    [InlineData(MigrationStep.RolledBack)]
    public void A_finished_migration_is_never_runnable(MigrationStep step)
    {
        var migration = At(step);

        Assert.False(migration.IsRunnableAt(Now));
        Assert.True(migration.IsFinished);
    }

    [Fact]
    public void A_parked_migration_is_never_runnable()
    {
        // The rule the whole design rests on: a step whose outcome is unknown is not work, it is a
        // question — and only the reconciler, which reads both panels first, may answer it.
        var migration = At(MigrationStep.NeedsAttention);

        Assert.False(migration.IsRunnableAt(Now));
        Assert.False(migration.IsFinished);
    }

    [Fact]
    public void A_step_waiting_out_its_backoff_is_not_runnable_yet()
    {
        var migration = At(MigrationStep.Creating);
        migration.NextAttemptAt = Now.AddSeconds(30);

        Assert.False(migration.IsRunnableAt(Now));
        Assert.True(migration.IsRunnableAt(Now.AddSeconds(31)));
    }

    [Fact]
    public void A_step_out_of_attempts_is_not_runnable()
    {
        var migration = At(MigrationStep.Creating);
        migration.Attempts = ServiceMigration.MaxAttempts;

        Assert.False(migration.IsRunnableAt(Now));
    }

    // ------------------------------------------------------------------------- dual active ----

    [Fact]
    public void Nothing_is_dual_active_before_the_destination_is_verified()
    {
        // The stamp is what makes it true, and it is only written once the destination has been read
        // back. Before that the customer has exactly one working client, whatever the panels contain.
        foreach (var step in new[]
                 {
                     MigrationStep.Planned, MigrationStep.Creating, MigrationStep.Verifying,
                 })
        {
            Assert.False(At(step).IsDualActive);
        }
    }

    [Fact]
    public void Detaching_with_a_stamp_is_dual_active()
    {
        var migration = At(MigrationStep.Detaching);
        migration.DualActiveSince = Now.AddMinutes(-3);

        Assert.True(migration.IsDualActive);
        Assert.Equal(TimeSpan.FromMinutes(3), migration.DualActiveFor(Now));
    }

    [Fact]
    public void A_migration_parked_after_verification_is_still_dual_active()
    {
        // The state that most needs to be visible: the panel went quiet mid-detach, so the customer
        // is being counted by two panels and nothing is advancing on its own.
        var migration = At(MigrationStep.NeedsAttention);
        migration.DualActiveSince = Now.AddMinutes(-20);

        Assert.True(migration.IsDualActive);
        Assert.Equal(TimeSpan.FromMinutes(20), migration.DualActiveFor(Now));
    }

    [Fact]
    public void A_completed_migration_is_no_longer_dual_active()
    {
        var migration = At(MigrationStep.Completed);
        migration.DualActiveSince = Now.AddMinutes(-5);
        migration.CompletedAt = Now;

        Assert.False(migration.IsDualActive);

        // And the window stops being measured, rather than growing for ever in the operator's view.
        Assert.Null(migration.DualActiveFor(Now.AddHours(1)));
    }
}
