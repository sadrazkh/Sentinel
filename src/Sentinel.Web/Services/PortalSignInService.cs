using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Security;
using Sentinel.Application.Identity;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Security;
using Sentinel.Web.Security;

namespace Sentinel.Web.Services;

/// <summary>
/// The whole sign-in decision lives here rather than in the controller: account state checks,
/// lockout, session creation, auditing and the user-enumeration defences are one flow that
/// has to stay consistent, and a controller is the wrong place to keep it.
/// </summary>
public sealed class PortalSignInService : IPortalSignInService
{
    /// <summary>
    /// Stand-in used to burn a password hash when no account matches, so a failed sign-in for
    /// an unknown user takes about as long as one for a real user with a wrong password.
    /// Without it, response time alone reveals which identifiers exist.
    /// </summary>
    private static readonly ApplicationUser TimingDecoy = new()
    {
        Id = Guid.Empty,
        UserName = "sentinel:timing-decoy",
    };

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IUserSessionService _sessions;
    private readonly ILoginAttemptService _loginAttempts;
    private readonly IAuditService _audit;
    private readonly ISentinelDbContext _db;
    private readonly IClientContext _clientContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SentinelSecurityOptions _securityOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PortalSignInService> _logger;

    public PortalSignInService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IUserSessionService sessions,
        ILoginAttemptService loginAttempts,
        IAuditService audit,
        ISentinelDbContext db,
        IClientContext clientContext,
        IHttpContextAccessor httpContextAccessor,
        IOptions<SentinelSecurityOptions> securityOptions,
        TimeProvider timeProvider,
        ILogger<PortalSignInService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _sessions = sessions;
        _loginAttempts = loginAttempts;
        _audit = audit;
        _db = db;
        _clientContext = clientContext;
        _httpContextAccessor = httpContextAccessor;
        _securityOptions = securityOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SignInOutcome> SignInAsync(
        SignInRequest request,
        CancellationToken cancellationToken = default)
    {
        var identifier = request.Identifier.Trim();

        var user = await FindUserAsync(identifier, cancellationToken);

        if (user is null)
        {
            _userManager.PasswordHasher.HashPassword(TimingDecoy, request.Password);
            return await FailAsync(identifier, null, LoginFailureReason.UnknownUser, cancellationToken);
        }

        var now = _timeProvider.GetUtcNow();

        if (!AccountSignInRules.CanSignIn(user, now))
        {
            // Still hash, so a disabled account is not distinguishable by response time.
            _userManager.PasswordHasher.HashPassword(TimingDecoy, request.Password);

            var reason = user.Status == UserAccountStatus.Disabled
                ? LoginFailureReason.AccountDisabled
                : LoginFailureReason.AccountSuspended;

            return await FailAsync(identifier, user.Id, reason, cancellationToken);
        }

        // lockoutOnFailure drives Identity's counter, which is what turns repeated guesses
        // against one account into a temporary lockout.
        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return await FailAsync(identifier, user.Id, LoginFailureReason.LockedOut, cancellationToken);
        }

        if (!result.Succeeded)
        {
            var reason = result.IsNotAllowed ? LoginFailureReason.NotAllowed : LoginFailureReason.InvalidPassword;
            return await FailAsync(identifier, user.Id, reason, cancellationToken);
        }

        await EstablishSessionAsync(user, request.RememberMe, now, cancellationToken);

        await _loginAttempts.RecordAsync(identifier, user.Id, true, LoginFailureReason.None, cancellationToken);
        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.LoginSucceeded, nameof(ApplicationUser), user.Id) with
            {
                ActorUserIdOverride = user.Id,
                ActorUserNameOverride = user.UserName,
            },
            cancellationToken);

        return SignInOutcome.Success;
    }

    private async Task EstablishSessionAsync(
        ApplicationUser user,
        bool rememberMe,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Session fixation: drop whatever cookie the browser arrived with before issuing a
        // new one, so a pre-set identifier can never survive the privilege change.
        await _signInManager.SignOutAsync();

        var lifetime = TimeSpan.FromMinutes(_securityOptions.SessionLifetimeMinutes);
        var session = await _sessions.CreateAsync(user.Id, lifetime, cancellationToken);

        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            IssuedUtc = now,
            ExpiresUtc = now.Add(lifetime),
            AllowRefresh = _securityOptions.SlidingExpiration,
        };

        // The session id travels in the cookie so every later request can verify the
        // server-side row is still live.
        await _signInManager.SignInWithClaimsAsync(
            user,
            properties,
            [new Claim(UserSession.ClaimType, session.Id.ToString())]);

        await _db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.LastLoginAt, now), cancellationToken);
    }

    public async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null
            || _clientContext.UserId is not { } userId
            || _clientContext.SessionId is not { } sessionId)
        {
            return;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            return;
        }

        // The existing properties are carried over so a "remember me" cookie does not quietly
        // become a session cookie, and the original expiry is preserved.
        var authentication = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        await _signInManager.SignInWithClaimsAsync(
            user,
            authentication.Properties,
            [new Claim(UserSession.ClaimType, sessionId.ToString())]);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = _clientContext.SessionId;
        var userId = _clientContext.UserId;
        var userName = _clientContext.UserName;

        if (sessionId is { } id)
        {
            // Revoking the row is what makes sign-out real: clearing the client's cookie
            // alone would leave a copied cookie usable until it expired.
            await _sessions.RevokeAsync(id, SessionRevocationReason.UserLogout, cancellationToken);
        }

        await _signInManager.SignOutAsync();

        if (userId is { } actorId)
        {
            await _audit.RecordAndSaveAsync(
                AuditEntry.For(AuditActions.Logout, nameof(ApplicationUser), actorId) with
                {
                    ActorUserIdOverride = actorId,
                    ActorUserNameOverride = userName,
                },
                cancellationToken);
        }
    }

    public async Task SignOutEverywhereAsync(CancellationToken cancellationToken = default)
    {
        var userId = _clientContext.UserId;
        var userName = _clientContext.UserName;

        if (userId is not { } actorId)
        {
            await _signInManager.SignOutAsync();
            return;
        }

        var revoked = await _sessions.RevokeAllForUserAsync(
            actorId, SessionRevocationReason.LogoutAllDevices, exceptSessionId: null, cancellationToken);

        // Rotating the security stamp invalidates any cookie Identity would otherwise still
        // accept, including ones issued outside the session table.
        var user = await _userManager.FindByIdAsync(actorId.ToString());
        if (user is not null)
        {
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _signInManager.SignOutAsync();

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.LogoutAllDevices, nameof(ApplicationUser), actorId) with
            {
                ActorUserIdOverride = actorId,
                ActorUserNameOverride = userName,
                Metadata = AuditMetadata.Create().Set("sessionsRevoked", revoked),
            },
            cancellationToken);
    }

    /// <summary>
    /// Accepts a username, an e-mail address or a phone number, tried in that order. Each is
    /// an indexed equality match on a normalised column — never string-concatenated SQL.
    /// </summary>
    private async Task<ApplicationUser?> FindUserAsync(string identifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var byName = await _userManager.FindByNameAsync(identifier);
        if (byName is not null)
        {
            return byName;
        }

        if (identifier.Contains('@', StringComparison.Ordinal))
        {
            return await _userManager.FindByEmailAsync(identifier);
        }

        // Normalisation is what makes "۰۹۱۲…", "0912…" and "+98912…" all find the same account.
        var normalizedPhone = PhoneNumberNormalizer.Normalize(identifier);

        return normalizedPhone is null
            ? null
            : await _userManager.Users
                .FirstOrDefaultAsync(u => u.NormalizedPhoneNumber == normalizedPhone, cancellationToken);
    }

    private async Task<SignInOutcome> FailAsync(
        string identifier,
        Guid? userId,
        LoginFailureReason reason,
        CancellationToken cancellationToken)
    {
        await _loginAttempts.RecordAsync(identifier, userId, false, reason, cancellationToken);

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.LoginFailed, nameof(ApplicationUser), userId) with
            {
                Result = AuditResult.Failure,
                ActorUserIdOverride = userId,
                ActorUserNameOverride = null,
                // The identifier is recorded; the submitted password never is.
                Metadata = AuditMetadata.Create()
                    .Set("reason", reason.ToString())
                    .Set("identifier", identifier),
            },
            cancellationToken);

        _logger.LogInformation(
            "Sign-in refused for identifier {Identifier} from {IpAddress}: {Reason}.",
            identifier,
            _clientContext.IpAddress,
            reason);

        return SignInOutcome.Failed(reason);
    }
}
