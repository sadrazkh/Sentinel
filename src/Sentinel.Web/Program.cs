using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.WebEncoders;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Options;
using Sentinel.Domain.Identity;
using Sentinel.Infrastructure;
using Sentinel.Infrastructure.Media;
using Sentinel.Infrastructure.Persistence;
using Sentinel.Infrastructure.Seeding;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;
using Sentinel.Web.Security;
using Sentinel.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------
// Logging. Structured from the first line, so startup failures are searchable too.
// ---------------------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Sentinel.Portal"));

// Kestrel announces itself by default; there is no reason to tell the world what to attack.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// ---------------------------------------------------------------------------------------
// Configuration binding. Every options object is validated, and validated at *start-up*
// rather than on first use, so a bad value cannot lurk until the first request.
// ---------------------------------------------------------------------------------------
builder.Services.AddOptions<SentinelSecurityOptions>()
    .Bind(builder.Configuration.GetSection(SentinelSecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MembershipOptions>()
    .Bind(builder.Configuration.GetSection(MembershipOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SecurityHeaderOptions>()
    .Bind(builder.Configuration.GetSection(SecurityHeaderOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<SeedOptions>()
    .Bind(builder.Configuration.GetSection(SeedOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<MediaStorageOptions>()
    .Bind(builder.Configuration.GetSection(MediaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The provider must be known before the DbContext is registered, so this one section is also
// read eagerly. ConnectionStrings:Sentinel wins, so hosts that only inject connection strings
// (containers, PaaS) work without a second setting.
var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();

if (builder.Configuration.GetConnectionString("Sentinel") is { Length: > 0 } connectionString)
{
    databaseOptions.ConnectionString = connectionString;
}

var securityOptions = builder.Configuration
    .GetSection(SentinelSecurityOptions.SectionName)
    .Get<SentinelSecurityOptions>() ?? new SentinelSecurityOptions();

var seedOptions = builder.Configuration
    .GetSection(SeedOptions.SectionName)
    .Get<SeedOptions>() ?? new SeedOptions();

using (var startupLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole()))
{
    StartupGuards.EnsureProductionSafety(
        builder.Environment,
        databaseOptions,
        securityOptions,
        seedOptions,
        startupLoggerFactory.CreateLogger("Sentinel.Startup"));
}

// ---------------------------------------------------------------------------------------
// Core services
// ---------------------------------------------------------------------------------------

// One clock for the whole application. Services take TimeProvider rather than calling
// DateTimeOffset.UtcNow, which is what makes expiry and grace-period logic testable.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IClientContext, HttpClientContext>();
builder.Services.AddScoped<IPortalSignInService, PortalSignInService>();

builder.Services.AddSentinelPersistence(databaseOptions);
builder.Services.AddSentinelInfrastructure();

// Cookies are encrypted with data-protection keys. In a container the default key ring lives
// in the writable layer and disappears on redeploy, signing everybody out; persisting it to a
// mounted volume is what keeps sessions alive across restarts and across replicas.
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Sentinel.Portal");
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    Directory.CreateDirectory(keyRingPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}

// ---------------------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------------------
builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        var password = securityOptions.Password;
        options.Password.RequiredLength = password.MinimumLength;
        options.Password.RequiredUniqueChars = password.RequiredUniqueChars;
        options.Password.RequireDigit = password.RequireDigit;
        options.Password.RequireLowercase = password.RequireLowercase;
        options.Password.RequireUppercase = password.RequireUppercase;
        options.Password.RequireNonAlphanumeric = password.RequireNonAlphanumeric;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = securityOptions.Lockout.MaxFailedAttempts;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(securityOptions.Lockout.LockoutMinutes);

        options.User.RequireUniqueEmail = true;

        // There is no mail transport in this version, so requiring confirmation would lock
        // everyone out. Administrators create accounts; self-service sign-up does not exist.
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;
        options.SignIn.RequireConfirmedPhoneNumber = false;
    })
    .AddEntityFrameworkStores<SentinelDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    // The __Host- prefix pins the cookie to this exact origin: a browser refuses it unless it
    // is Secure, path-wide and domain-less, which stops a sibling subdomain from overwriting
    // it. It requires HTTPS, so the plain-HTTP test host falls back to the plain name.
    options.Cookie.Name = securityOptions.RequireHttps ? "__Host-sentinel.auth" : "sentinel.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = securityOptions.RequireHttps
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;

    // Lax, not Strict: the cookie must survive a top-level redirect back from an external
    // application. It still blocks the cross-site POST that CSRF depends on, and the
    // anti-forgery token covers the rest.
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
    options.Cookie.Path = "/";

    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ReturnUrlParameter = "returnUrl";

    options.ExpireTimeSpan = TimeSpan.FromMinutes(securityOptions.SessionLifetimeMinutes);
    options.SlidingExpiration = securityOptions.SlidingExpiration;

    // Session revocation and account-status checks run here, on every request.
    options.EventsType = typeof(SessionValidationCookieEvents);
});

builder.Services.AddScoped<SessionValidationCookieEvents>();

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(15));

builder.Services.AddSentinelAuthorization();
builder.Services.AddSentinelRateLimiting();

// ---------------------------------------------------------------------------------------
// MVC, anti-forgery and localisation
// ---------------------------------------------------------------------------------------
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = securityOptions.RequireHttps ? "__Host-sentinel.csrf" : "sentinel.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = securityOptions.RequireHttps
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;

    // Lets fetch/axios send the token in a header instead of a form field.
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddControllersWithViews(options =>
{
    // Every unsafe verb is validated by default. Relying on each action to remember
    // [ValidateAntiForgeryToken] means one forgotten attribute is one CSRF hole.
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());

    // Inserted ahead of the default simple-type binder. Without it, the Persian culture reads
    // the ISO date an <input type="date"> submits through the Persian calendar and stores a
    // year six centuries out. See Iso8601DateModelBinder.
    options.ModelBinderProviders.Insert(0, new Iso8601DateModelBinderProvider());
});

// By default the HTML encoder only lets Basic Latin through unescaped, so every Persian
// character is emitted as a numeric entity — correct, but it roughly triples the size of a
// Persian page and makes the markup unreadable. Widening the allow-list to the Arabic script
// costs nothing in safety: '<', '>', '&', '"' and '\'' are still encoded, and the response
// declares charset=utf-8, which is what the Basic-Latin default is guarding against.
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(
        UnicodeRanges.BasicLatin,
        UnicodeRanges.Latin1Supplement,
        UnicodeRanges.Arabic,
        UnicodeRanges.ArabicSupplement,
        UnicodeRanges.ArabicExtendedA,
        UnicodeRanges.ArabicPresentationFormsA,
        UnicodeRanges.ArabicPresentationFormsB,
        UnicodeRanges.GeneralPunctuation));

builder.Services.AddSingleton<LocalizationStore>();
builder.Services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
builder.Services.AddSingleton(typeof(IStringLocalizer<>), typeof(StringLocalizer<>));

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supported = PortalCultures.All.Select(culture => new CultureInfo(culture)).ToList();

    options.DefaultRequestCulture = new RequestCulture(PortalCultures.Persian);
    options.SupportedCultures = supported;
    options.SupportedUICultures = supported;
    options.ApplyCurrentCultureToResponseHeaders = true;
});

// ---------------------------------------------------------------------------------------
// Health checks. Liveness answers "is the process up"; readiness also proves the database is
// reachable, so an orchestrator does not route traffic to an instance that cannot serve.
// ---------------------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SentinelDbContext>("database", tags: ["ready"]);

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;

    // Preload is a one-way door: browsers ship the list in their binary and removal takes
    // months. Opt in only once the domain is certain to stay HTTPS-only.
    options.Preload = false;
});

builder.Services.AddHttpsRedirection(options =>
    options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect);

if (securityOptions.ForwardedHeaderHops > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = securityOptions.ForwardedHeaderHops;

        // Cleared deliberately: the defaults trust only loopback, and a container's proxy is
        // never on loopback. Enable this only when a proxy you control is the sole ingress —
        // otherwise a client can spoof its own source address in the audit log.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

// ---------------------------------------------------------------------------------------
// Pipeline. Order matters: correlation id first so every later log line carries it, then
// security headers so they reach error responses and static files as well.
// ---------------------------------------------------------------------------------------
app.UseMiddleware<CorrelationIdMiddleware>();

if (securityOptions.ForwardedHeaderHops > 0)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Production never shows exception detail: the user gets a correlation id, the log gets
    // the stack trace.
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/error/{0}");

if (securityOptions.RequireHttps)
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "public,max-age=3600",
});

app.UseSerilogRequestLogging(options =>
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= 500
            ? Serilog.Events.LogEventLevel.Error
            : Serilog.Events.LogEventLevel.Information);

app.UseRequestLocalization();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Areas first: the admin area's routes must match before the catch-all default pattern.
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Users}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    // No checks registered: this only answers "is the process responding".
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

await app.InitializeDatabaseAsync();

app.Run();

/// <summary>Exposed so the integration test host can boot the real application.</summary>
public partial class Program;
