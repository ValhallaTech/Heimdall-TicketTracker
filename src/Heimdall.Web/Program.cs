using System;
using System.Threading.RateLimiting;
using AspNetCore.DataProtection.CustomStorage.Dapper.PostgreSQL;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Heimdall.BLL.Email;
using Heimdall.Core.Models;
using Heimdall.DAL.Caching;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Extensions;
using Heimdall.DAL.Migrations;
using Heimdall.Web.Authentication;
using Heimdall.Web.Authorization;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Components;
using Heimdall.Web.DependencyInjection;
using Heimdall.Web.Email;
using Heimdall.Web.Endpoints;
using Heimdall.Web.Identity;
using Heimdall.Web.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Serilog ---------------------------------------------------------------
// Clear default MEL providers (Console/Debug/EventSource/EventLog) so Serilog is the only sink.
builder.Logging.ClearProviders();
builder.Host.UseSerilog(
    (context, services, loggerConfig) =>
        loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
);

// --- Kestrel / $PORT binding ----------------------------------------------
// Render and many PaaS providers inject PORT. Fall back to 8080 for local dev.
var portEnv = Environment.GetEnvironmentVariable("PORT");
var port =
    !string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var configuredPort)
        ? configuredPort
        : 8080;
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

// --- Connection strings (DATABASE_URL, REDIS_URL) --------------------------
var databaseUrl =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("Postgres");
var redisUrl =
    Environment.GetEnvironmentVariable("REDIS_URL")
    ?? builder.Configuration.GetConnectionString("Redis");

var postgresConnectionString =
    ConnectionStringTranslator.ToNpgsqlConnectionString(databaseUrl)
    ?? throw new InvalidOperationException(
        "DATABASE_URL (or ConnectionStrings:Postgres) must be set to a valid Postgres connection."
    );

var redisConnectionString =
    ConnectionStringTranslator.ToRedisConfiguration(redisUrl)
    ?? throw new InvalidOperationException(
        "REDIS_URL (or ConnectionStrings:Redis) must be set to a valid Redis configuration."
    );

builder.Services.Configure<DataOptions>(options =>
{
    options.PostgresConnectionString = postgresConnectionString;
    options.RedisConnectionString = redisConnectionString;
});

// --- Redis multiplexer (singleton) ----------------------------------------
// AbortOnConnectFail = false so a transient Redis outage at startup degrades gracefully
// (the cache layer's catches turn cache misses into DB hits) instead of crashing the app.
// SyncTimeout / ConnectTimeout are left at the StackExchange.Redis defaults (5s each)
// — explicitly NOT 0 / infinite. ClientName surfaces this app in `CLIENT LIST` / Redis
// monitoring, mirroring the Application Name we set on the Postgres side.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    options.ClientName = "Heimdall.Web";
    return ConnectionMultiplexer.Connect(options);
});

// --- FluentMigrator -------------------------------------------------------
builder.Services.AddHeimdallMigrations(postgresConnectionString);

// --- Data access layer (Dapper.Extensions for PostgreSQL) ------------------
builder.Services.AddDal();

// --- Forwarded headers (proxy-aware HTTPS detection) ----------------------
// Render (and most PaaS hosts) terminate TLS at an upstream proxy and forward
// plain HTTP to the app with X-Forwarded-Proto: https. Without this the app
// would see Scheme=http for genuinely-HTTPS requests, breaking redirect URL
// generation, the SecurePolicy=SameAsRequest cookie decision, and any "is
// this secure?" check downstream. KnownNetworks/KnownProxies are cleared
// because the proxy is in a routable address range we can't enumerate; this
// is the documented Microsoft pattern for hosting behind an opaque PaaS proxy.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// --- ASP.NET Core Identity (cookie scheme) --------------------------------
// Phase 1 step 4 of docs/proposals/security-and-authorization.md §9.3. Order
// matters: migrations → DAL → identity (Identity stores resolve through the
// Dapper-backed HeimdallUserStore registered by AddHeimdallIdentityStores).
//
// Scope is intentionally narrow:
//   * No JWT bearer scheme — that lands in Phase 5.
//   * No [Authorize] attributes / global auth gate — step 9 owns that.
//   * No login/logout endpoints, no IEmailSender — steps 6-7 own those.
//   * No AddRoles<...>() — RBAC was dropped in PR #25.
// Just the wiring required for Identity to be usable by later steps.
builder.Services.AddHeimdallIdentityStores();

builder
    .Services.AddIdentityCore<HeimdallUser>(options =>
    {
        // Email is the login identifier; collisions are not allowed.
        options.User.RequireUniqueEmail = true;

        // Strong-but-reasonable password policy. At lease 12 chars + 4 character
        // classes + 1 distinct chars rejects the common low-entropy patterns
        // (e.g. "Password1!") without pushing users into password-manager-only
        // territory.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 1;

        // Lockout: 5 strikes / 15 minutes, applied to brand-new users too so an
        // attacker cannot bypass it by targeting freshly-created accounts.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // Phase 1 keeps onboarding simple — email-gated reset/register lands in
        // step 10 once IEmailSender is wired in step 6. Flipping these to true
        // before then would brick first-run sign-in.
        options.SignIn.RequireConfirmedEmail = false;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddSignInManager()

    // Default token providers (DataProtector / Email / Phone / Authenticator).
    // Required by the password-reset and email-confirmation flows that land in
    // step 10; registered now so the DI graph is stable across phases.
    .AddDefaultTokenProviders();

// Cookie auth scheme. AddIdentityCore deliberately does NOT register one — we
// opt in here with the canonical Identity scheme name so SignInManager's calls
// to HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, ...) resolve
// to this handler.
builder
    .Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(
        IdentityConstants.ApplicationScheme,
        options =>
        {
            // Endpoints land in step 7; the paths are reserved here so 401s
            // redirect somewhere sensible the moment those pages exist.
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/access-denied";

            // 8h ≈ a working day. Sliding refresh keeps an active user signed
            // in; the SecurityStamp-based RevalidatingServerAuthenticationStateProvider
            // (step 5) will still kick out a session whose stamp has changed.
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;

            options.Cookie.HttpOnly = true;

            // SameAsRequest (rather than Always) so the auth cookie survives plain
            // HTTP scenarios that the deployment topology genuinely uses HTTP for —
            // primarily the in-process TestServer used by the Phase 1 acceptance
            // suite. Production traffic always reaches the app over HTTPS:
            // app.UseHttpsRedirection() (registered below in non-Development) plus
            // Render's TLS-terminating proxy + ForwardedHeaders middleware ensure
            // the request scheme is "https" and so the cookie is minted Secure.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            // Lax (not Strict) so OAuth / external-login callbacks in later
            // phases — which round-trip through a third-party origin — still
            // carry the cookie back. Strict would silently break those flows.
            options.Cookie.SameSite = SameSiteMode.Lax;

            // Predictable name (instead of the framework's GUID-suffixed
            // default) so ops tooling, log filters, and the browser devtools
            // can recognise the auth cookie at a glance.
            options.Cookie.Name = ".Heimdall.Auth";
        }
    );

// Authorization services + the global authenticated-only fallback policy
// (Phase 1 step 9 of docs/proposals/security-and-authorization.md §9.3). The
// fallback applies to every endpoint without its own authorization metadata;
// public pages (login, access-denied, error/not-found, splash) opt out with
// [AllowAnonymous]. Phase 3 will layer OpenFGA-backed resource checks on top.
//
// The configuration delegate lives in AuthorizationConfiguration.Configure
// so the focused unit tests in Heimdall.Web.Tests.Authorization apply the
// exact same options as production startup.
builder.Services.AddAuthorization(AuthorizationConfiguration.Configure);

// --- Rate limiting (Phase 1 step 7 / §3.5) --------------------------------
// /account/login throttle keyed on (client IP, submitted username) per §3.5:
// IP alone enables credential-stuffing through botnets; username alone hands
// attackers a free username-based lockout DoS. Combining both narrows the
// limit to a (source, target) pair.
//
// The policy callback is sync — RateLimitPartition.GetFixedWindowLimiter is
// not awaitable. Rather than sync-blocking on ReadFormAsync().GetAwaiter()
// (which would sync-over-async on body I/O if the form isn't buffered yet
// and risks threadpool starvation under load), the email is pre-extracted
// asynchronously by RateLimitFormEmailMiddleware (registered earlier in the
// pipeline) and stashed in HttpContext.Items. The callback below just reads
// that value synchronously.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string submittedEmail =
            httpContext.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] as string
            ?? string.Empty;
        string key = $"{ip}|{submittedEmail.ToLowerInvariant()}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // Password-reset throttle (Phase 1 step 10). Different threat model from
    // login: an attacker spamming forgot-password gets a free way to flood a
    // victim's inbox and burn token-provider entropy. Tighter than login (5
    // permits / 10 minutes) and keyed identically on (ip|submitted-email).
    options.AddPolicy("password-reset", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string submittedEmail =
            httpContext.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] as string
            ?? string.Empty;
        string key = $"{ip}|{submittedEmail.ToLowerInvariant()}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// --- Data Protection key persistence (PostgreSQL) -------------------------
// Render scales horizontally, so each replica MUST share the Data Protection
// key ring or antiforgery / auth cookies minted by one replica will be
// rejected by another. Persisting to Postgres (rather than the default
// per-instance file system) gives every replica the same view of the ring.
//
// InitializeTable = false: the package can auto-create its table at startup,
// but Heimdall owns its schema in FluentMigrator
// (M202605050003_CreateDataProtectionKeys) so deployments fail loudly on a
// missing migration instead of silently drifting between environments.
//
// SetApplicationName isolates this app's keys from any other app sharing the
// same database — required by Data Protection's purpose-string contract.
builder
    .Services.AddDataProtection()
    .PersistKeysWithDapperInPostgreSQL(
        postgresConnectionString,
        config => config.InitializeTable = false
    )
    .SetApplicationName("Heimdall");

// --- Blazor ---------------------------------------------------------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Per-circuit revalidating auth state provider: re-checks the user's
// security_stamp every RevalidationInterval (5 min) so password resets,
// force-logouts, and account disables propagate to live SignalR circuits
// without waiting for the user to navigate. Must be registered scoped
// because the base RevalidatingServerAuthenticationStateProvider tracks
// per-circuit timer state. See docs/proposals/security-and-authorization.md
// §3.4 / §9.3 step 5.
builder.Services.AddScoped<AuthenticationStateProvider, HeimdallRevalidatingAuthenticationStateProvider>();

// Cascades AuthenticationState to every component so <AuthorizeView> and
// [CascadingParameter] AuthenticationState work without manual plumbing.
builder.Services.AddCascadingAuthenticationState();

// --- Email seam (Phase 1 step 6) ------------------------------------------
// Registers IEmailSender. Picks MailKitEmailSender when Email:Smtp Host /
// UserName / Password / From are all configured; otherwise falls back to
// NoOpEmailSender so non-SMTP environments boot cleanly. The chosen
// implementation is logged below after Build().
builder.Services.AddHeimdallEmail(builder.Configuration);

// EmailFlowGate (Phase 1 step 10) — surfaces whether email-driven self-service
// flows are usable. Singleton because EmailSenderRegistrationInfo is also a
// singleton; both are decided at startup and never change at runtime.
builder.Services.AddSingleton<EmailFlowGate>();

// Self-service registration toggle (Phase 1 step 10). Default Enabled=false;
// the operator must explicitly opt in via the "Registration" config section
// (and SMTP must be configured) before the /register page becomes reachable.
builder.Services.AddOptions<RegistrationOptions>()
    .Bind(builder.Configuration.GetSection("Registration"));

// --- SystemAdmin bootstrap registration (Phase 1 step 8) ------------------
// Resolved per-scope from the startup bootstrap block below. Scoped lifetime
// matches the Identity stores it composes (UserManager, IUserStore).
builder.Services.AddScoped<SystemAdminBootstrapper>();

// --- Default-hierarchy bootstrap registration (Phase 2.3 step 9) ----------
// Resolved per-scope from the startup bootstrap block below, immediately after
// SystemAdminBootstrapper so the bootstrap admin's user-id exists before this
// runs. Idempotent on every startup.
builder.Services.AddScoped<DefaultHierarchyBootstrapper>();

// --- Autofac --------------------------------------------------------------
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
    containerBuilder.RegisterModule<ApplicationModule>()
);

var app = builder.Build();

// --- Run migrations at startup --------------------------------------------
try
{
    await app.Services.RunHeimdallMigrationsAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Database migration failed on startup.");
    throw;
}

// --- SystemAdmin bootstrap (Phase 1 step 8) -------------------------------
// On first deploy to a fresh DB, create an initial admin from env vars so an
// operator can sign in and start managing users. Both env vars must be set;
// missing / empty values are a no-op. Idempotent: re-running on an existing
// admin is a no-op; running on an existing non-admin email promotes it.
//
// Failures here are logged but never abort startup — a transient DB hiccup
// at boot must not take the whole app down.
var bootstrapEmail = Environment.GetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_EMAIL");
var bootstrapPassword = Environment.GetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD");
try
{
    using var bootstrapScope = app.Services.CreateAsyncScope();
    var bootstrapper = bootstrapScope.ServiceProvider.GetRequiredService<SystemAdminBootstrapper>();
    await bootstrapper.RunAsync(bootstrapEmail, bootstrapPassword).ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Error(ex, "SystemAdmin bootstrap raised an unexpected exception; continuing startup.");
}

// --- Default-hierarchy bootstrap (Phase 2.3 step 9) -----------------------
// Sequencing note: cannot run as a FluentMigrator migration on a fresh DB —
// bootstrap_admin.id does not exist until SystemAdminBootstrapper above runs.
// Idempotent on every startup, keyed on slug for parents and on
// (user_id, parent_id) for membership rows. Failures are logged-and-swallowed
// (same policy as SystemAdminBootstrapper) so a transient DB hiccup at boot
// does not abort startup.
try
{
    using var hierarchyScope = app.Services.CreateAsyncScope();
    var hierarchyBootstrapper = hierarchyScope.ServiceProvider.GetRequiredService<DefaultHierarchyBootstrapper>();
    await hierarchyBootstrapper.RunAsync(bootstrapEmail).ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Error(ex, "Default-hierarchy bootstrap raised an unexpected exception; continuing startup.");
}

// --- Email sender choice (Phase 1 step 6) ---------------------------------
// Surfaces which IEmailSender implementation was wired up. The Reason text is
// secret-free by construction (see EmailServiceCollectionExtensions).
var emailInfo = app.Services.GetRequiredService<EmailSenderRegistrationInfo>();
Log.Information(
    "Email sender: {Implementation} ({Reason})",
    emailInfo.ChosenImplementation,
    emailInfo.Reason);

// --- Seed database unless explicitly opted out (SEED_DATABASE=false) -------
var seedEnv = Environment.GetEnvironmentVariable("SEED_DATABASE")?.Trim();
var seedDatabase =
    string.IsNullOrWhiteSpace(seedEnv)
    || !string.Equals(seedEnv, "false", StringComparison.OrdinalIgnoreCase);
var seedCountEnv = Environment.GetEnvironmentVariable("SEED_COUNT");
var seedCount =
    !string.IsNullOrWhiteSpace(seedCountEnv) && int.TryParse(seedCountEnv, out var parsedCount)
        ? parsedCount
        : 50;
try
{
    await app.Services.SeedIfRequestedAsync(seedDatabase, postgresConnectionString, seedCount);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Database seeding failed on startup.");
    throw;
}

// --- Pipeline -------------------------------------------------------------
// ForwardedHeaders MUST run first — every downstream component (HSTS,
// HttpsRedirection, SecurePolicy=SameAsRequest, redirect URL generation) keys
// off Request.Scheme, and the only correct value behind a TLS-terminating
// proxy comes from X-Forwarded-Proto. See ForwardedHeadersOptions registration
// above; KnownNetworks/KnownProxies are intentionally empty for opaque PaaS
// proxies (Render).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();

    // HTTPS-only in production. Skipped under Development so the in-process
    // TestServer used by Phase1AcceptanceTests (which runs over plain HTTP)
    // is not 307'd into an unreachable HTTPS endpoint.
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSerilogRequestLogging();

// Pipeline order: routing → authentication → authorization → antiforgery →
// static assets → razor components. UseAuthentication MUST precede
// UseAntiforgery so antiforgery tokens can be bound to an authenticated
// identity once login lands in step 7. UseAuthorization is registered now to
// keep the DI graph stable; with no [Authorize] attributes or fallback policy
// in place (step 9) it is effectively a no-op for unauthenticated requests
// and will not break the existing public Tickets pages.
app.UseAuthentication();
app.UseAuthorization();
// Pre-extract the form-submitted email on (ip|email)-keyed rate-limited POSTs
// so the synchronous rate-limit policy callbacks (login, password-reset) can
// read it from HttpContext.Items without sync-over-async on body I/O. Must
// run before UseRateLimiter so the policy observes the stashed value.
app.UseRateLimitFormEmailExtraction();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// --- Account endpoints (Phase 1 step 7) -----------------------------------
// Server-rendered POST /account/login + /account/logout. Mapped after the
// Razor components so they participate in the same routing namespace, and
// after UseAntiforgery so form posts go through antiforgery validation.
app.MapAccountEndpoints();

app.Run();

/// <summary>Exposes the Program class for integration testing.</summary>
public partial class Program;
