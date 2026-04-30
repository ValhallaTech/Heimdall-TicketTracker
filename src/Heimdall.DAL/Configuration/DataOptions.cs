namespace Heimdall.DAL.Configuration;

/// <summary>
/// Bound configuration used to construct Npgsql / Redis connections at startup.
/// </summary>
public class DataOptions
{
    /// <summary>Configuration section name used by <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.</summary>
    public const string SectionName = "Data";

    /// <summary>Fully-formed Npgsql connection string (translated from DATABASE_URL if needed).</summary>
    public string PostgresConnectionString { get; set; } = string.Empty;

    /// <summary>StackExchange.Redis configuration string (host:port[,options]).</summary>
    public string RedisConnectionString { get; set; } = string.Empty;
}
