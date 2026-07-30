using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Billing;
using Sentinel.Application.Common;
using Sentinel.Application.Features;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Billing;
using Sentinel.Domain.Common;

namespace Sentinel.Infrastructure.Billing;

/// <summary>
/// The credit ledger.
/// <para>
/// Every movement is the same shape: read the wallet, decide, append an immutable entry, and write
/// the new balance under the wallet's concurrency token. The token is what makes the decision and
/// the write one operation — without it, two debits reading a balance of 100 could both subtract 80
/// and leave −60.
/// </para>
/// <para>
/// Idempotency is enforced by a unique index on (wallet, reference), not by a prior read. A read
/// would still let two replicas both find nothing and both insert; the index makes the second one
/// fail, and that failure is caught and turned into "you already did this".
/// </para>
/// </summary>
public sealed class WalletService : IWalletService
{
    /// <summary>
    /// Attempts before giving up on a contended wallet. A movement is one small write, so a genuine
    /// collision clears on the next read; more attempts than this means something is wrong.
    /// </summary>
    private const int MaxAttempts = 3;

    private readonly ISentinelDbContext _db;
    private readonly IAuditService _audit;
    private readonly IFeatureGate _features;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WalletService> _logger;

    public WalletService(
        ISentinelDbContext db,
        IAuditService audit,
        IFeatureGate features,
        TimeProvider timeProvider,
        ILogger<WalletService> logger)
    {
        _db = db;
        _audit = audit;
        _features = features;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The feature gate, checked in the service and not only on the controller.
    /// <para>
    /// A flag that only guards endpoints is a flag a background job or a future caller walks
    /// straight past. This is money, so the refusal lives where the money moves.
    /// </para>
    /// </summary>
    private bool Enabled => _features.IsEnabled(FeatureNames.Wallet);

    public async Task<OperationResult<WalletView>> GetOrCreateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return OperationResult<WalletView>.Failure(WalletErrors.Disabled);
        }

        var wallet = await LoadOrCreateAsync(userId, cancellationToken);

        if (wallet is null)
        {
            return OperationResult<WalletView>.Failure(WalletErrors.MemberNotFound);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<WalletView>.Success(ToView(wallet));
    }

    public async Task<IReadOnlyList<WalletHolderView>> ListHoldersAsync(
        string? search = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return [];
        }

        take = Math.Clamp(take, 1, 200);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        // Driven from the member list rather than the wallet table, so somebody who has never been
        // credited is still findable — which is exactly who an operator is usually looking for.
        var members = _db.Users.AsNoTracking();

        if (search is not null)
        {
            members = members.Where(user =>
                user.UserName!.Contains(search)
                || user.DisplayName.Contains(search)
                || (user.Email != null && user.Email.Contains(search)));
        }

        var rows = await members
            .OrderBy(user => user.UserName)
            .Take(take)
            .Select(user => new { user.Id, user.UserName, user.DisplayName })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Id).ToList();

        var wallets = await _db.Wallets
            .AsNoTracking()
            .Where(wallet => ids.Contains(wallet.UserId))
            .Select(wallet => new
            {
                wallet.UserId,
                wallet.BalanceMinorUnits,
                wallet.Currency,
                wallet.IsFrozen,
                wallet.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var byUser = wallets.ToDictionary(wallet => wallet.UserId);

        return rows
            .Select(row =>
            {
                byUser.TryGetValue(row.Id, out var wallet);

                return new WalletHolderView(
                    row.Id,
                    row.UserName ?? "—",
                    row.DisplayName,
                    wallet?.BalanceMinorUnits ?? 0,
                    wallet?.Currency ?? "IRR",
                    wallet?.IsFrozen ?? false,
                    wallet is not null,
                    wallet?.UpdatedAt);
            })
            .ToList();
    }

    public async Task<WalletLedger?> GetLedgerAsync(
        Guid userId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return null;
        }

        var wallet = await _db.Wallets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (wallet is null)
        {
            return null;
        }

        take = Math.Clamp(take, 1, 500);

        // Newest first for the query, oldest first for the caller: a ledger reads downwards, and
        // the consistency check compares the wallet against the *last* entry.
        var rows = await _db.WalletTransactions
            .AsNoTracking()
            .Where(entry => entry.WalletId == wallet.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        rows.Reverse();

        var ids = rows.Select(entry => entry.Id).ToList();

        // Which of these have been reversed. One extra query rather than a correlated subquery per
        // row, and it only has to cover the page being shown.
        var reversed = await _db.WalletTransactions
            .AsNoTracking()
            .Where(entry => entry.ReversesTransactionId != null
                            && ids.Contains(entry.ReversesTransactionId.Value))
            .Select(entry => entry.ReversesTransactionId!.Value)
            .ToListAsync(cancellationToken);

        var reversedSet = reversed.ToHashSet();

        var entries = rows
            .Select(entry => new WalletEntryView(
                entry.Id,
                entry.Kind,
                entry.Direction,
                entry.AmountMinorUnits,
                entry.BalanceAfterMinorUnits,
                entry.Currency,
                entry.Description,
                entry.ReversesTransactionId,
                reversedSet.Contains(entry.Id),
                entry.CreatedAt))
            .ToList();

        return new WalletLedger(ToView(wallet), entries);
    }

    // ------------------------------------------------------------------------- movements ----

    public Task<OperationResult<Guid>> CreditAsync(
        AdjustWalletRequest request,
        Guid performedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MoveAsync(
            request.UserId,
            WalletTransactionKind.OperatorCredit,
            WalletEntryDirection.Credit,
            request.AmountMinorUnits,
            request.Description,
            request.Reference,
            performedByUserId,
            relatedServiceId: null,
            WalletAuditActions.Credited,
            save: true,
            cancellationToken);
    }

    public Task<OperationResult<Guid>> DebitAsync(
        AdjustWalletRequest request,
        Guid performedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MoveAsync(
            request.UserId,
            WalletTransactionKind.OperatorDebit,
            WalletEntryDirection.Debit,
            request.AmountMinorUnits,
            request.Description,
            request.Reference,
            performedByUserId,
            relatedServiceId: null,
            WalletAuditActions.Debited,
            save: true,
            cancellationToken);
    }

    public async Task<OperationResult<Guid>> SpendAsync(
        Guid userId,
        long amountMinorUnits,
        string currency,
        string description,
        string? reference,
        Guid? relatedServiceId,
        CancellationToken cancellationToken = default)
    {
        var wallet = await _db.Wallets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        // Checked before the movement so the caller gets the specific reason. A wallet in another
        // currency is not a shortfall, and telling somebody they have insufficient funds when the
        // real problem is that their credit is in rials and the plan is priced in euros is the kind
        // of message that wastes a support hour.
        if (wallet is not null
            && !string.Equals(wallet.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<Guid>.Failure(WalletErrors.CurrencyMismatch);
        }

        return await MoveAsync(
            userId,
            WalletTransactionKind.Purchase,
            WalletEntryDirection.Debit,
            amountMinorUnits,
            description,
            reference,

            // No operator: the member is spending their own credit, and UserId already names them.
            performedByUserId: null,
            relatedServiceId,
            WalletAuditActions.Spent,

            // Left uncommitted on purpose. The caller saves it with whatever was bought, so the
            // ledger can never record a payment for a service that was not created.
            save: false,
            cancellationToken);
    }

    public async Task<OperationResult<Guid>> ReverseAsync(
        Guid transactionId,
        Guid performedByUserId,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return OperationResult<Guid>.Failure(WalletErrors.Disabled);
        }

        var original = await _db.WalletTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == transactionId, cancellationToken);

        if (original is null)
        {
            return OperationResult<Guid>.Failure(WalletErrors.EntryNotFound);
        }

        // Reversing a reversal would let two operators undo each other indefinitely and leave a
        // ledger nobody can read. Correct the original instead.
        if (original.Kind == WalletTransactionKind.Reversal)
        {
            return OperationResult<Guid>.Failure(WalletErrors.CannotReverseReversal);
        }

        var alreadyReversed = await _db.WalletTransactions.AnyAsync(
            entry => entry.ReversesTransactionId == transactionId, cancellationToken);

        if (alreadyReversed)
        {
            return OperationResult<Guid>.Failure(WalletErrors.AlreadyReversed);
        }

        // The opposite direction, the same amount. A reversal of a debit gives the credit back; a
        // reversal of a credit takes it away, and that one can be refused for want of funds — the
        // member may have already spent it, and this ledger does not go negative even to correct
        // itself. An operator then has to settle it another way, which is the honest outcome.
        var direction = original.Direction == WalletEntryDirection.Credit
            ? WalletEntryDirection.Debit
            : WalletEntryDirection.Credit;

        return await MoveAsync(
            original.UserId,
            WalletTransactionKind.Reversal,
            direction,
            original.AmountMinorUnits,
            description,

            // Derived from the original, so a reversal is idempotent for free: a retry hits the
            // unique index rather than appending a second one.
            reference: $"reversal:{transactionId:N}",
            performedByUserId,
            original.RelatedServiceId,
            WalletAuditActions.Reversed,
            save: true,
            cancellationToken,
            reversesTransactionId: transactionId);
    }

    public async Task<OperationResult> SetFrozenAsync(
        Guid userId,
        bool frozen,
        string? reason,
        Guid performedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return OperationResult.Failure(WalletErrors.Disabled);
        }

        var wallet = await LoadOrCreateAsync(userId, cancellationToken);

        if (wallet is null)
        {
            return OperationResult.Failure(WalletErrors.MemberNotFound);
        }

        wallet.IsFrozen = frozen;
        wallet.FrozenReason = frozen && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null;

        await _audit.RecordAsync(
            AuditEntry.For(
                frozen ? WalletAuditActions.Frozen : WalletAuditActions.Unfrozen,
                nameof(Wallet),
                wallet.Id) with
            {
                ActorUserIdOverride = performedByUserId,
                Metadata = AuditMetadata.Create().Set("userId", userId),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    // --------------------------------------------------------------------------- the core ----

    /// <summary>
    /// One movement: decide and write under the wallet's token, appending an immutable entry.
    /// <para>
    /// Every public method funnels through here so the rules — positive amounts, no overdraft,
    /// idempotent on the reference, balance and entry written together — exist once rather than
    /// once per caller.
    /// </para>
    /// </summary>
    private async Task<OperationResult<Guid>> MoveAsync(
        Guid userId,
        WalletTransactionKind kind,
        WalletEntryDirection direction,
        long amountMinorUnits,
        string? description,
        string? reference,
        Guid? performedByUserId,
        Guid? relatedServiceId,
        string auditAction,
        bool save,
        CancellationToken cancellationToken,
        Guid? reversesTransactionId = null)
    {
        if (!Enabled)
        {
            return OperationResult<Guid>.Failure(WalletErrors.Disabled);
        }

        // Zero is not a movement and a negative amount is a direction expressed the wrong way.
        // Both are refused rather than normalised, because both mean the caller is confused.
        if (amountMinorUnits <= 0)
        {
            return OperationResult<Guid>.Failure(WalletErrors.AmountInvalid);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var wallet = await LoadOrCreateAsync(userId, cancellationToken);

            if (wallet is null)
            {
                return OperationResult<Guid>.Failure(WalletErrors.MemberNotFound);
            }

            // Answered from the ledger, before anything is decided: a repeated request must return
            // the entry it already made rather than making a second one.
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var existing = await _db.WalletTransactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        entry => entry.WalletId == wallet.Id && entry.Reference == reference,
                        cancellationToken);

                if (existing is not null)
                {
                    return OperationResult<Guid>.Success(existing.Id);
                }
            }

            if (direction == WalletEntryDirection.Debit)
            {
                if (wallet.IsFrozen)
                {
                    return OperationResult<Guid>.Failure(WalletErrors.Frozen);
                }

                // The rule that makes this a wallet and not a tab. Checked here, inside the guarded
                // write, so a concurrent spend cannot slip between the check and the update.
                if (wallet.BalanceMinorUnits < amountMinorUnits)
                {
                    return OperationResult<Guid>.Failure(WalletErrors.InsufficientFunds);
                }
            }

            var now = _timeProvider.GetUtcNow();

            var delta = direction == WalletEntryDirection.Credit ? amountMinorUnits : -amountMinorUnits;
            var balanceAfter = wallet.BalanceMinorUnits + delta;

            var entry = new WalletTransaction
            {
                Id = SequentialGuid.New(now),
                WalletId = wallet.Id,
                UserId = userId,
                Kind = kind,
                Direction = direction,
                AmountMinorUnits = amountMinorUnits,
                BalanceAfterMinorUnits = balanceAfter,
                Currency = wallet.Currency,
                Description = Truncate(description, WalletTransaction.DescriptionMaxLength),
                Reference = Truncate(reference, WalletTransaction.ReferenceMaxLength),
                ReversesTransactionId = reversesTransactionId,
                PerformedByUserId = performedByUserId,
                RelatedServiceId = relatedServiceId,
                CreatedAt = now,
            };

            _db.WalletTransactions.Add(entry);
            wallet.BalanceMinorUnits = balanceAfter;

            await _audit.RecordAsync(
                AuditEntry.For(auditAction, nameof(WalletTransaction), entry.Id) with
                {
                    ActorUserIdOverride = performedByUserId,
                    Metadata = AuditMetadata.Create()
                        .Set("userId", userId)
                        .Set("kind", kind)
                        .Set("amountMinorUnits", amountMinorUnits)
                        .Set("balanceAfterMinorUnits", balanceAfter)
                        .Set("currency", wallet.Currency),
                },
                cancellationToken);

            // The caller commits, so the debit lands in the same transaction as the purchase.
            if (!save)
            {
                return OperationResult<Guid>.Success(entry.Id);
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);

                return OperationResult<Guid>.Success(entry.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody else moved this wallet first. The tracked copy still carries the old
                // token, so it has to be re-read — and the decision has to be made again, because
                // the balance it was based on is no longer the balance.
                _db.Detach(entry);
                await _db.ReloadAsync(wallet, cancellationToken);

                _logger.LogDebug(
                    "Wallet movement for {UserId} lost a race (attempt {Attempt}).", userId, attempt);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Two callers raced with the same idempotency key and this one lost. The winner's
                // entry is the answer — which is exactly what the key was asking for.
                _db.Detach(entry);
                await _db.ReloadAsync(wallet, cancellationToken);

                var winner = await _db.WalletTransactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        candidate => candidate.WalletId == wallet.Id && candidate.Reference == reference,
                        cancellationToken);

                if (winner is not null)
                {
                    return OperationResult<Guid>.Success(winner.Id);
                }

                throw;
            }
        }

        _logger.LogWarning(
            "Could not move credit for {UserId} after {Attempts} attempts.", userId, MaxAttempts);

        return OperationResult<Guid>.Failure(WalletErrors.Contended);
    }

    // --------------------------------------------------------------------------- helpers ----

    private async Task<Wallet?> LoadOrCreateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (wallet is not null)
        {
            return wallet;
        }

        // Only for a member who exists. Creating a wallet for an invented id would let a caller
        // seed rows by guessing.
        if (!await _db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        wallet = new Wallet
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            BalanceMinorUnits = 0,
            Currency = "IRR",
        };

        _db.Wallets.Add(wallet);

        return wallet;
    }

    private static WalletView ToView(Wallet wallet) =>
        new(
            wallet.Id,
            wallet.UserId,
            wallet.BalanceMinorUnits,
            wallet.Currency,
            wallet.IsFrozen,
            wallet.FrozenReason,
            wallet.UpdatedAt);

    /// <summary>
    /// Whether a write failed on a unique index.
    /// <para>
    /// Matched on the provider's own message because the exception type is the same for every
    /// constraint. Both engines this portal supports are covered; a miss falls through and rethrows,
    /// which is the safe direction — a swallowed write error on money would be far worse than a
    /// noisy one.
    /// </para>
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;

        return message.Contains("23505", StringComparison.Ordinal)               // PostgreSQL
               || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               || message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= max ? value.Trim() : value.Trim()[..max];
}
