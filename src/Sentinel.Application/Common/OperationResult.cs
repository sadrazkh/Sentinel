namespace Sentinel.Application.Common;

/// <summary>
/// The outcome of an administrative operation.
/// <para>
/// Failures carry a localisation key rather than a message, so the application layer never
/// decides what language an operator reads, and never returns a raw exception string to a
/// view. Expected failures — a name already taken, a stale edit — are values here rather than
/// exceptions, because they are ordinary outcomes, not faults.
/// </para>
/// </summary>
public record OperationResult
{
    protected OperationResult(bool succeeded, string? errorKey, IReadOnlyList<string>? details)
    {
        Succeeded = succeeded;
        ErrorKey = errorKey;
        Details = details ?? [];
    }

    public bool Succeeded { get; }

    public string? ErrorKey { get; }

    /// <summary>Extra machine-readable detail, such as Identity's validation codes.</summary>
    public IReadOnlyList<string> Details { get; }

    public static OperationResult Success() => new(true, null, null);

    public static OperationResult Failure(string errorKey, IReadOnlyList<string>? details = null) =>
        new(false, errorKey, details);
}

public sealed record OperationResult<T> : OperationResult
{
    private OperationResult(bool succeeded, T? value, string? errorKey, IReadOnlyList<string>? details)
        : base(succeeded, errorKey, details) =>
        Value = value;

    public T? Value { get; }

    public static OperationResult<T> Success(T value) => new(true, value, null, null);

    public static new OperationResult<T> Failure(string errorKey, IReadOnlyList<string>? details = null) =>
        new(false, default, errorKey, details);
}

/// <summary>Localisation keys for the failures the admin services can return.</summary>
public static class OperationErrors
{
    public const string NotFound = "admin.error.notFound";
    public const string UserNameTaken = "admin.error.userNameTaken";
    public const string EmailTaken = "admin.error.emailTaken";
    public const string PhoneTaken = "admin.error.phoneTaken";
    public const string InvalidPhone = "admin.error.invalidPhone";
    public const string PasswordRejected = "admin.error.passwordRejected";
    public const string IdentityRejected = "admin.error.identityRejected";
    public const string ConcurrencyConflict = "admin.error.concurrencyConflict";
    public const string InvalidDateRange = "admin.error.invalidDateRange";
    public const string UnknownRole = "admin.error.unknownRole";
    public const string CannotRemoveOwnAdminRole = "admin.error.cannotRemoveOwnAdminRole";
    public const string CannotChangeOwnStatus = "admin.error.cannotChangeOwnStatus";
    public const string LastSuperAdmin = "admin.error.lastSuperAdmin";
}
