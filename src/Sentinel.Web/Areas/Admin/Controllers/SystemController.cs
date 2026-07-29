using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sentinel.Application.Authorization;
using Sentinel.Application.Options;
using Sentinel.Application.Settings;
using Sentinel.Application.Users;
using Sentinel.Infrastructure.Media;
using Sentinel.Infrastructure.Persistence;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Security;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// System overview: live counters, the effective non-secret configuration, and the role list.
/// <para>
/// Read-only. Settings are shown, never edited here: they come from environment variables and
/// a secret store, and a screen that wrote them back would need somewhere to write them to —
/// which means a second, mutable source of truth for security-critical values.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.SystemAdministration)]
public sealed class SystemController : Controller
{
    private readonly ISystemOverviewQuery _overview;
    private readonly IRoleSummaryQuery _roles;
    private readonly IWebHostEnvironment _environment;
    private readonly DatabaseOptions _databaseOptions;
    private readonly SentinelSecurityOptions _securityOptions;
    private readonly MembershipOptions _membershipOptions;
    private readonly MediaStorageOptions _mediaOptions;

    public SystemController(
        ISystemOverviewQuery overview,
        IRoleSummaryQuery roles,
        IWebHostEnvironment environment,
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<SentinelSecurityOptions> securityOptions,
        IOptions<MembershipOptions> membershipOptions,
        IOptions<MediaStorageOptions> mediaOptions)
    {
        _overview = overview;
        _roles = roles;
        _environment = environment;
        _databaseOptions = databaseOptions.Value;
        _securityOptions = securityOptions.Value;
        _membershipOptions = membershipOptions.Value;
        _mediaOptions = mediaOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var counters = await _overview.GetCountersAsync(cancellationToken);
        var roles = await _roles.ListAsync(cancellationToken);

        return View(new SystemSettingsViewModel
        {
            Counters = counters,
            Settings = BuildSettings(),
            Roles = roles.Select(r => new RoleSummaryRow(r.Name, r.Description, r.MemberCount)).ToList(),
            EnvironmentName = _environment.EnvironmentName,
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "—",
        });
    }

    /// <summary>
    /// The effective configuration, as values an operator can act on.
    /// <para>
    /// Every entry here is deliberately chosen. Connection strings, the data-protection key
    /// path, and the seed password are absent — not merely masked — because the safest way to
    /// avoid disclosing a secret on a page is never to put it on the page.
    /// </para>
    /// </summary>
    private IReadOnlyList<SettingRow> BuildSettings()
    {
        var invariant = CultureInfo.InvariantCulture;

        return
        [
            new("Database:Provider",
                _databaseOptions.Provider.ToString(),
                "admin.system.setting.provider", false),

            new("Database:MigrateOnStartup",
                Format(_databaseOptions.MigrateOnStartup),
                "admin.system.setting.migrateOnStartup", true),

            new("Database:EnableSensitiveDataLogging",
                Format(_databaseOptions.EnableSensitiveDataLogging),
                "admin.system.setting.sensitiveLogging", true),

            new("Security:RequireHttps",
                Format(_securityOptions.RequireHttps),
                "admin.system.setting.requireHttps", true),

            new("Security:SessionLifetimeMinutes",
                _securityOptions.SessionLifetimeMinutes.ToString(invariant),
                "admin.system.setting.sessionLifetime", false),

            new("Security:ForwardedHeaderHops",
                _securityOptions.ForwardedHeaderHops.ToString(invariant),
                "admin.system.setting.forwardedHops", true),

            new("Security:Password:MinimumLength",
                _securityOptions.Password.MinimumLength.ToString(invariant),
                "admin.system.setting.passwordLength", false),

            new("Security:Lockout:MaxFailedAttempts",
                _securityOptions.Lockout.MaxFailedAttempts.ToString(invariant),
                "admin.system.setting.lockoutAttempts", false),

            new("Security:LoginRateLimit:PermitLimit",
                $"{_securityOptions.LoginRateLimit.PermitLimit} / {_securityOptions.LoginRateLimit.WindowSeconds}s",
                "admin.system.setting.rateLimit", false),

            new("Membership:GracePeriodDays",
                _membershipOptions.GracePeriodDays.ToString(invariant),
                "admin.system.setting.gracePeriod", false),

            new("Membership:RenewalWarningDays",
                _membershipOptions.RenewalWarningDays.ToString(invariant),
                "admin.system.setting.renewalWarning", false),

            new("MediaStorage:MaxIconBytes",
                $"{_mediaOptions.MaxIconBytes / 1024} KB",
                "admin.system.setting.maxIcon", false),
        ];
    }

    private static string Format(bool value) => value ? "true" : "false";
}
