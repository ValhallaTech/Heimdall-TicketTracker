using System;
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
using Heimdall.Web.Components;
using Heimdall.Web.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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

        // Strong-but-reasonable password policy. 12 chars + four character
        // classes + 4 distinct chars rejects the common low-entropy patterns
        // (e.g. "Password1!") without pushing users into password-manager-only
        // territory.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 4;

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
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

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

// Authorization services only. Policy registration is intentionally deferred
// to step 9 — Phase 1 has no [Authorize] attributes or global gate.
builder.Services.AddAuthorization();

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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Exposes the Program class for integration testing.</summary>
public partial class Program;
