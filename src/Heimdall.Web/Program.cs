using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AspNetCore.DataProtection.CustomStorage.Dapper.PostgreSQL;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Email;
using Heimdall.Core.Models;
using Heimdall.DAL.Caching;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Extensions;
using Heimdall.DAL.Migrations;
using Heimdall.Web.Authentication;
using Heimdall.Web.Authorization;
using Heimdall.Web.Authorization.Policies;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Components;
using Heimdall.Web.DependencyInjection;
using Heimdall.Web.Email;
using Heimdall.Web.Endpoints;
using Heimdall.Web.Identity;
using Heimdall.Web.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
// Timeouts are pinned explicitly (rather than relying on StackExchange.Redis defaults) so
// they remain stable across client upgrades and so a slow Redis cannot stall a request
// thread for longer than ConnectTimeout / SyncTimeout / AsyncTimeout. ClientName surfaces
// this app in `CLIENT LIST` / Redis monitoring, mirroring the Application Name we set on
// the Postgres side. ConnectRetry + KeepAlive give us faster recovery from transient
// network blips between Render's web service and its Key-Value (Redis) service.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    options.ClientName = "Heimdall.Web";
    options.ConnectTimeout = 5_000;
    options.SyncTimeout = 5_000;
    options.AsyncTimeout = 5_000;
    options.ConnectRetry = 3;
    options.KeepAlive = 60;
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

        // Phase 4.3 step 7 — name the TOTP token provider so
        // UserManager.{Get,Reset,Verify}AuthenticatorKey/Token resolve to the
        // explicit AuthenticatorTokenProvider<HeimdallUser> registration below.
        // Sets the same name (TokenOptions.DefaultAuthenticatorProvider) that
        // AddDefaultTokenProviders uses, so the two registrations agree.
        options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
    })

    // Phase 4.3 step 7 — AddIdentityCore deliberately does NOT register
    // SignInManager (unlike AddIdentity); the explicit generic form here keeps
    // the registration auditable. SignInManager<HeimdallUser> is required by
    // the Phase 4.5 step 12 TwoFactorAuthenticatorSignInAsync challenge path.
    .AddSignInManager<SignInManager<HeimdallUser>>()

    // Token providers — registered explicitly (rather than via
    // AddDefaultTokenProviders) so the DI graph contains only providers
    // Heimdall actually supports. AddDefaultTokenProviders additionally
    // registers PhoneNumberTokenProvider, which calls
    // IUserPhoneNumberStore<TUser> from
    // SignInManager.GetValidTwoFactorProvidersAsync during every MFA login —
    // HeimdallUserStore deliberately does not implement that interface (no
    // phone-number column, no SMS transport), so registering it would crash
    // the login pipeline with NotSupportedException on the first 2FA sign-in.
    //
    //   - DataProtector  → password-reset / email-confirmation tokens
    //   - Email          → email-channel 2FA token surface (verify path uses
    //                      IUserEmailStore which we DO implement)
    //   - Authenticator  → TOTP MFA (Phase 4.3 step 7 — re-registered below
    //                      to keep the audit trail required by the checklist)
    .AddTokenProvider<DataProtectorTokenProvider<HeimdallUser>>(TokenOptions.DefaultProvider)
    .AddTokenProvider<EmailTokenProvider<HeimdallUser>>(TokenOptions.DefaultEmailProvider)

    // Phase 4.3 step 7 — explicit AuthenticatorTokenProvider<HeimdallUser>
    // registration under TokenOptions.DefaultAuthenticatorProvider. Kept as
    // the single source of truth that Heimdall consumes a TOTP provider.
    .AddTokenProvider<AuthenticatorTokenProvider<HeimdallUser>>(
        TokenOptions.DefaultAuthenticatorProvider);

// Phase 4.3 step 9 — QR-code renderer used by /account/mfa/setup. Singleton
// because the underlying QRCoder generator is stateless and re-allocating it
// per request is pure waste.
builder.Services.AddSingleton<IAuthenticatorQrCodeRenderer, AuthenticatorQrCodeRenderer>();

// Phase 4.3 step 10 — one-shot in-memory cache that carries freshly-generated
// recovery codes across the verify-POST → display-GET redirect. Backed by
// IMemoryCache (registered here defensively in case no other component has).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRecoveryCodeDisplayCache, RecoveryCodeDisplayCache>();

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
    )
    // Phase 4.5 step 12 — short-lived cookie that carries the
    // TwoFactorUserId across the password→challenge hop. SignInManager
    // signs into this scheme from PasswordSignInAsync when
    // RequiresTwoFactor, and reads it back from
    // TwoFactorAuthenticatorSignInAsync. Identity's defaults are fine:
    // session-lifetime cookie, HttpOnly, SameSite=Lax.
    .AddCookie(IdentityConstants.TwoFactorUserIdScheme, options =>
    {
        options.Cookie.Name = ".Heimdall.TwoFactorUserId";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    // Phase 4.5 step 12 — long-lived "remember this device" cookie used
    // when the user opts into rememberMachine on the challenge form.
    // Registered so SignInManager.RememberTwoFactorClientAsync /
    // ForgetTwoFactorClientAsync resolve a scheme; the actual lifetime
    // is controlled by Identity's TwoFactorRememberMe expiration option.
    .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, options =>
    {
        options.Cookie.Name = ".Heimdall.TwoFactorRememberMe";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
    })

    // Phase 5.3 step 6 — JWT bearer scheme. ApplicationScheme remains the
    // default (the browser / Blazor circuit signs in via the cookie); this
    // scheme is only consumed by [Authorize(AuthenticationSchemes =
    // JwtBearerDefaults.AuthenticationScheme)] endpoints in the Phase 5.4 /
    // 5.5 API surface. Issuer + audience come from the Phase 5.1 TokenOptions
    // section. The IssuerSigningKeyResolver delegates to ISigningKeyService so
    // a rotated key set is picked up without restarting the bearer middleware.
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // Phase 5.1 TokenOptions are bound below; we capture them here via
        // PostConfigure so the resolver sees the same Issuer/Audience that
        // TokenOptionsValidator enforced at startup.
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,

            // Hardening §2.1 / RFC 8725 §3.1: never accept HS* or "none". The
            // resolver below also fails closed when the kid is unknown, but the
            // explicit allow-list short-circuits any attempt to swap algorithms
            // before the resolver is consulted.
            ValidAlgorithms = new[] { "RS256", "ES256" },

            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // PostConfigure runs after TokenOptions are bound, so Issuer/Audience
        // are populated. Doing the wire-up here (instead of inside AddJwtBearer)
        // means a misconfigured Token section fails at startup with the
        // TokenOptionsValidator message rather than a deferred 401 chain.
    });
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IServiceProvider>((options, sp) =>
    {
        // Re-resolved per-application-startup; safe to capture sp because the
        // JwtBearer middleware itself is request-scoped through
        // IHttpContextAccessor for its other needs.
        var tokenOptions = sp.GetRequiredService<IOptions<Heimdall.Core.Tokens.TokenOptions>>().Value;
        options.TokenValidationParameters.ValidIssuer = tokenOptions.Issuer;
        options.TokenValidationParameters.ValidAudience = tokenOptions.Audience;

        // IssuerSigningKeyResolver — return only the key matching the JWT's
        // kid (or the full trusted set when the JWT did not carry one). The
        // delegate is synchronous; ISigningKeyService.GetTrustedKeysAsync is
        // async but talks to a Phase 5.1 in-process IMemoryCache layer in
        // front of the DB read, so the sync-over-async cost is negligible
        // and bounded. The middleware caches our response per (kid, token)
        // for the duration of the validation, not across requests.
        options.TokenValidationParameters.IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
        {
            using var scope = sp.CreateScope();
            var signingKeys = scope.ServiceProvider.GetRequiredService<Heimdall.BLL.Tokens.ISigningKeyService>();
            var trusted = signingKeys.GetTrustedKeysAsync().GetAwaiter().GetResult();

            var keys = new System.Collections.Generic.List<SecurityKey>(trusted.Count);
            foreach (var record in trusted)
            {
                if (!string.IsNullOrEmpty(kid) && !string.Equals(record.Kid, kid, StringComparison.Ordinal))
                {
                    continue;
                }

                SecurityKey? materialised = MaterialisePublicSecurityKey(record);
                if (materialised is not null)
                {
                    keys.Add(materialised);
                }
            }

            return keys;
        };
    });

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

// Phase 3.5 (docs/proposals/openfga.md §3 step 9 + step 10) — register the
// OpenFGA + system-admin authorization handlers and IHttpContextAccessor.
// The handlers consume scoped dependencies (FGA adapter, audit writer,
// user lookup) and are themselves scoped; the ASP.NET policy pipeline
// resolves them via the request scope.
builder.Services.AddHeimdallAuthorizationPolicies();

// Phase 4.6 step 15 — seed-organization id resolution. The accessor is a
// singleton mutable holder, populated by either ConfigureSeedOrganizationOptions
// (env var path) or DefaultHierarchyBootstrapper (fresh-DB path). The
// SeedOrganizationHealthProbe below aborts startup if neither path resolves a
// value. Phase 4.6 step 16 RequireMfaAuthorizationHandler reads the id via
// IOptionsMonitor<SeedOrganizationOptions>.
builder.Services.AddSingleton<SeedOrganizationAccessor>();
builder.Services.AddOptions<SeedOrganizationOptions>();
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IConfigureOptions<SeedOrganizationOptions>, ConfigureSeedOrganizationOptions>());
builder.Services.AddScoped<SeedOrganizationHealthProbe>();

// Phase 4.6 step 17 — custom authorization-result handler that redirects admins
// missing MFA to /account/mfa/setup instead of emitting a 403. Registered as a
// singleton (the handler is stateless and the default base type is also a
// singleton). Replaces the framework default AuthorizationMiddlewareResultHandler.
builder.Services.AddSingleton<
    IAuthorizationMiddlewareResultHandler,
    MfaSetupRedirectMiddlewareResultHandler>();

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

    // Phase 4.3 step 10 — MFA setup / disable throttle keyed on (ip, user_id).
    // Tighter than login (10 permits / 5 minutes) to neuter brute-force
    // guessing of the fresh authenticator secret window. user_id rather than
    // submitted-email because both MFA endpoints run inside an authenticated
    // cookie session — the principal is already established.
    options.AddPolicy("mfa-setup", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string userId =
            httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        string key = $"{ip}|{userId}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // Phase 4.5 step 12 — MFA challenge / recovery throttle. The challenge
    // endpoint is reached on the post-password 2FA hand-off, BEFORE the
    // ApplicationScheme cookie is issued — so httpContext.User is anonymous
    // at the policy callback. Identity stores the pending 2FA user id in a
    // cookie whose name is the public constant IdentityConstants.TwoFactorUserIdScheme;
    // its value is the encrypted auth ticket and therefore unique per pending
    // 2FA session. We partition on (ip, sha256(cookie-value)) so two users
    // behind the same NAT cannot exhaust each other's challenge budget. Hash
    // (not the raw cookie) keeps the partition key short and avoids leaking
    // ticket bytes into limiter state. When the cookie is absent (the only
    // legitimate caller without it is a noise request that the endpoint will
    // 302 to /login anyway) we still partition by ip so noise cannot escape
    // the limiter.
    options.AddPolicy("mfa-challenge", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string? twoFactorCookie =
            httpContext.Request.Cookies[IdentityConstants.TwoFactorUserIdScheme];
        string sessionFingerprint;
        if (string.IsNullOrEmpty(twoFactorCookie))
        {
            sessionFingerprint = "no-2fa-cookie";
        }
        else
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(twoFactorCookie));
            // First 16 hex chars (8 bytes) is ample to disambiguate concurrent
            // 2FA sessions on the same IP without ballooning limiter state.
            sessionFingerprint = Convert.ToHexString(hash, 0, 8);
        }
        string key = $"{ip}|{sessionFingerprint}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // Phase 5.3 step 6 — generic API-token throttle. Composes with the "login"
    // limiter on /api/v1/auth/token (both must allow) and is the sole limiter
    // on /api/v1/auth/refresh. Partitioned on (ip, sub) when the request
    // already carries an authenticated principal (mirroring a future bearer
    // call) and on (ip,) otherwise, so an attacker cycling refresh-cookie
    // values from a single host cannot exceed the ceiling by rotating
    // identities. 60 permits / minute is generous enough for legitimate SPA
    // burst patterns without making credential-stuffing attractive.
    options.AddPolicy("api-token", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // MapInboundClaims = false on the JwtBearer scheme means the JWT "sub"
        // claim is not remapped to ClaimTypes.NameIdentifier; read it directly.
        string sub =
            httpContext.User?.FindFirstValue("sub") ?? string.Empty;
        string key = string.IsNullOrEmpty(sub) ? ip : $"{ip}|{sub}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
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

// --- Token / signing-key options (Phase 5.1 step 2) -----------------------
// Strongly-typed Heimdall.Core.Tokens.TokenOptions bound to the "Token"
// config section. Carries the access-token lifetime and the signing-key
// rotation/overlap windows that SigningKeyService consults to enforce the
// hardening proposal §2.5 overlap-window invariant. TokenOptionsValidator
// runs at startup (ValidateOnStart) so a misconfigured floor — e.g.
// SigningKeyOverlap < AccessTokenLifetime — fails fast instead of silently
// shrinking the safety margin.
builder.Services
    .AddOptions<Heimdall.Core.Tokens.TokenOptions>()
    .Bind(builder.Configuration.GetSection("Token"))
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<Heimdall.Core.Tokens.TokenOptions>,
    Heimdall.BLL.Tokens.TokenOptionsValidator>();

// --- Phase 5.1 step 2/3: signing-key service + JWKS cache invalidator -----
// JWKS cache invalidator is a singleton (stateless; just removes one key from
// IMemoryCache). SigningKeyService is scoped because it composes scoped DAL
// repositories and a per-request IDataProtector view. Hardening §2.1 forbids
// caching the decrypted key in a longer-lived component.
builder.Services.AddSingleton<Heimdall.BLL.Tokens.IJwksCacheInvalidator,
    Heimdall.BLL.Tokens.MemoryCacheJwksCacheInvalidator>();
builder.Services.AddScoped<Heimdall.BLL.Tokens.ISigningKeyService,
    Heimdall.BLL.Tokens.SigningKeyService>();

// --- Phase 5.3 step 7: JWT access-token issuer ----------------------------
// Scoped (matches the SigningKeyService it composes). The issuer resolves
// SigningCredentialsResult once per IssueAccessTokenAsync call and disposes
// it inside the method — no decrypted key outlives a single call (hardening
// §2.1). Refresh-token plaintext / hash generation is also exposed here so
// the password-grant and refresh-rotation endpoints share one recipe.
builder.Services.AddScoped<Heimdall.BLL.Tokens.ITokenIssuer,
    Heimdall.BLL.Tokens.JwtTokenIssuer>();

// --- SystemAdmin bootstrap registration (Phase 1 step 8) ------------------
// Resolved per-scope from the startup bootstrap block below. Scoped lifetime
// matches the Identity stores it composes (UserManager, IUserStore).
builder.Services.AddScoped<SystemAdminBootstrapper>();

// --- Default-hierarchy bootstrap registration (Phase 2.3 step 9) ----------
// Resolved per-scope from the startup bootstrap block below, immediately after
// SystemAdminBootstrapper so the bootstrap admin's user-id exists before this
// runs. Idempotent on every startup.
builder.Services.AddScoped<DefaultHierarchyBootstrapper>();

// --- Ticket-defaults backfill registration (Phase 2.4 / 2.5 steps 10–14) --
// Populates tickets.project_id / team_id / reporter_id on legacy rows so the
// matching NOT NULL flip migrations succeed. Resolved per-scope immediately
// after DefaultHierarchyBootstrapper so the seed hierarchy exists first.
builder.Services.AddScoped<TicketDefaultsBackfiller>();

// --- OpenFGA registration (Phase 3.3 + 3.4) -------------------------------
// Registers the OpenFga.Sdk client, IOptions<OpenFgaOptions> bound to the
// "Authorization:OpenFga" config section with env-var post-binding per
// render.yaml, the in-memory cache used by the check adapter, and the
// IOpenFgaAuthorizationService / ITupleWriter / OpenFgaBackfillJob
// implementations themselves. Everything is registered on the MEL service
// collection and forwarded into Autofac by AutofacServiceProviderFactory so
// all OpenFGA wiring lives in one place — including the no-op fall-backs
// installed when OPENFGA_API_URL / store id / model id are unset, which
// keeps dev and unit-test stacks bootable without a sidecar.
builder.Services.AddHeimdallOpenFga(builder.Configuration);

// Health probe + backfill runner for the OpenFGA sidecar. Both are scoped so
// they participate in the per-startup bootstrap scope below.
builder.Services.AddScoped<OpenFgaHealthProbe>();
builder.Services.AddScoped<OpenFgaBackfillRunner>();

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
    await bootstrapper.RunAsync(bootstrapEmail, bootstrapPassword, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}
// OCE propagates so cooperative host shutdown is honoured.
catch (Exception ex) when (ex is not OperationCanceledException)
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
    await hierarchyBootstrapper.RunAsync(bootstrapEmail, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}
// OCE propagates so cooperative host shutdown is honoured.
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Error(ex, "Default-hierarchy bootstrap raised an unexpected exception; continuing startup.");
}

// --- Seed-organization health probe (Phase 4.6 step 15) -------------------
// After the bootstrapper has populated SeedOrganizationAccessor (and/or the
// env-var path has resolved one already), force-rebuild the
// SeedOrganizationOptions snapshot and abort startup if the resolved id is
// still empty. A missing id would silently disable the Phase 4.6 step 16
// RequireMfa policy — admins could reach /admin/* without MFA — so the
// failure mode is intentionally loud.
try
{
    using var seedOrgProbeScope = app.Services.CreateScope();
    var seedOrgProbe = seedOrgProbeScope.ServiceProvider.GetRequiredService<SeedOrganizationHealthProbe>();
    _ = seedOrgProbe.Run();
}
catch (InvalidOperationException ex)
{
    Log.Fatal(ex, "Seed-organization health probe failed; aborting startup.");
    throw;
}

// --- Ticket-defaults backfill (Phase 2.4 / 2.5 steps 10–14) ---------------
// Populates tickets.project_id / team_id / reporter_id on legacy rows so the
// next deploy's NOT NULL flip migrations (M202605050022 / M202605050024) can
// succeed. Sequenced immediately after DefaultHierarchyBootstrapper so the
// seed hierarchy + bootstrap admin already exist. Idempotent: each UPDATE is
// keyed on "… IS NULL", so re-runs against a fully-backfilled table are
// no-ops. Failures are logged-and-swallowed (same policy as the bootstrappers
// above); the matching NOT NULL flip migrations will fail loudly on the next
// deploy if any row was missed, which is the intended safety net.
try
{
    using var backfillScope = app.Services.CreateAsyncScope();
    var ticketBackfiller = backfillScope.ServiceProvider.GetRequiredService<TicketDefaultsBackfiller>();
    await ticketBackfiller.RunAsync(bootstrapEmail, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}
// OCE propagates so cooperative host shutdown is honoured.
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Error(ex, "Ticket-defaults backfill raised an unexpected exception; continuing startup.");
}

// --- OpenFGA health probe (Phase 3.3 step 9) ------------------------------
// When OPENFGA_HEALTH_PROBE_ENABLED=true (or the corresponding config flag is
// set), validate the configured sidecar + store + authorization-model triplet
// before any traffic is served. A failure here is intentional: hard-fail
// startup so misconfiguration is loud, not silent. When disabled the probe is
// a no-op so dev environments and pre-cutover deploys do not need a reachable
// sidecar.
using (var openFgaProbeScope = app.Services.CreateAsyncScope())
{
    var openFgaProbe = openFgaProbeScope.ServiceProvider.GetRequiredService<OpenFgaHealthProbe>();
    await openFgaProbe.RunAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}

// --- OpenFGA backfill runner (Phase 3.4 step 8) ---------------------------
// Gated on HEIMDALL_OPENFGA_BACKFILL=1. Idempotent across runs (per-tuple
// "already exists" errors are absorbed by ITupleWriter as audit events). Logs
// a structured BackfillResult on success. Failures are swallowed: the backfill
// can be retried by re-running the deploy with the env var set, so a transient
// sidecar outage during one rollout does not block the app from coming up.
try
{
    using var openFgaBackfillScope = app.Services.CreateAsyncScope();
    var openFgaBackfillRunner = openFgaBackfillScope.ServiceProvider.GetRequiredService<OpenFgaBackfillRunner>();
    await openFgaBackfillRunner.RunAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}
// OCE propagates so cooperative host shutdown is honoured.
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Error(ex, "OpenFGA backfill runner raised an unexpected exception; continuing startup.");
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

// --- API auth endpoints (Phase 5.4 steps 9-10) ---------------------------
// JSON-bodied /api/v1/auth/token (password grant) and /api/v1/auth/refresh
// (refresh rotation with family-replay detection). Mapped here so they share
// the same routing namespace + middleware pipeline as the browser flow.
app.MapApiAuthEndpoints();

// --- JWKS endpoint (Phase 5.1 step 3) ------------------------------------
// Publishes the public halves of the trusted signing keys at
// /.well-known/jwks.json. Anonymous; response cached for 5 minutes with
// in-process invalidation on rotation via IJwksCacheInvalidator.
app.MapJwksEndpoint();

app.Run();

/// <summary>Exposes the Program class for integration testing.</summary>
public partial class Program
{
    /// <summary>
    /// Builds a verification-only <see cref="SecurityKey"/> from a
    /// <see cref="Heimdall.Core.Tokens.SigningKeyRecord"/>'s public JWK. Used by
    /// the JwtBearer scheme's <c>IssuerSigningKeyResolver</c> to translate the
    /// Phase 5.1 <c>signing_keys</c> rows into the form the validator expects.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when the JWK is missing the required public fields,
    /// the base64url values are malformed, or <c>ImportParameters</c> rejects the
    /// key material (fail-closed — a corrupted row must not throw out of the
    /// resolver and produce a 500 during authentication).
    /// </remarks>
    private static SecurityKey? MaterialisePublicSecurityKey(Heimdall.Core.Tokens.SigningKeyRecord record)
    {
        if (record.Alg == Heimdall.Core.Tokens.SigningAlgorithm.Rs256)
        {
            if (string.IsNullOrEmpty(record.PublicJwk.N) || string.IsNullOrEmpty(record.PublicJwk.E))
            {
                return null;
            }

            var rsa = RSA.Create();
            try
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64UrlDecode(record.PublicJwk.N),
                    Exponent = Base64UrlDecode(record.PublicJwk.E),
                });
                return new RsaSecurityKey(rsa) { KeyId = record.Kid };
            }
            catch (Exception)
            {
                rsa.Dispose();
                return null;
            }
        }

        if (record.Alg == Heimdall.Core.Tokens.SigningAlgorithm.Es256)
        {
            if (string.IsNullOrEmpty(record.PublicJwk.X) || string.IsNullOrEmpty(record.PublicJwk.Y))
            {
                return null;
            }

            var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = Base64UrlDecode(record.PublicJwk.X),
                        Y = Base64UrlDecode(record.PublicJwk.Y),
                    },
                });
                return new ECDsaSecurityKey(ecdsa) { KeyId = record.Kid };
            }
            catch (Exception)
            {
                ecdsa.Dispose();
                return null;
            }
        }

        return null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - (padded.Length % 4)) % 4;
        if (padding > 0)
        {
            padded += new string('=', padding);
        }

        return Convert.FromBase64String(padded);
    }
}
