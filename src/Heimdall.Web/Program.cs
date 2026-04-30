using System;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Heimdall.DAL.Caching;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Extensions;
using Heimdall.DAL.Migrations;
using Heimdall.Web.Components;
using Heimdall.Web.DependencyInjection;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Serilog ---------------------------------------------------------------
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
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConnectionString)
);

// --- FluentMigrator -------------------------------------------------------
builder.Services.AddHeimdallMigrations(postgresConnectionString);

// --- Data access layer (Dapper.Extensions for PostgreSQL) ------------------
builder.Services.AddDal();

// --- Blazor ---------------------------------------------------------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Exposes the Program class for integration testing.</summary>
public partial class Program;
