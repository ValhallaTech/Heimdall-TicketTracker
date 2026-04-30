using System;
using Dapper.Extensions;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Configuration;

/// <summary>
/// <see cref="IConnectionStringProvider"/> implementation that resolves the PostgreSQL
/// connection string from the strongly-typed <see cref="DataOptions"/> contract instead of
/// reading directly from <c>ConnectionStrings:DefaultConnection</c> in <c>IConfiguration</c>.
/// </summary>
/// <remarks>
/// The application translates <c>DATABASE_URL</c> (and other PaaS-injected forms) into a
/// fully-formed Npgsql connection string at startup and binds it to <see cref="DataOptions"/>.
/// Routing Dapper.Extensions through this provider keeps every component sourcing the same
/// translated connection string and avoids regressing to raw configuration lookups.
/// </remarks>
public sealed class DataOptionsConnectionStringProvider : IConnectionStringProvider
{
    private readonly IOptions<DataOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataOptionsConnectionStringProvider"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <c>null</c>.</exception>
    public DataOptionsConnectionStringProvider(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Returns the configured PostgreSQL connection string for any requested connection name.
    /// </summary>
    /// <param name="connectionName">The logical connection name requested by Dapper.Extensions (ignored — this app uses a single connection).</param>
    /// <param name="enableMasterSlave">Master/slave switching is not used; ignored.</param>
    /// <param name="readOnly">Read-only routing is not used; ignored.</param>
    /// <returns>The fully-formed Npgsql connection string from <see cref="DataOptions.PostgresConnectionString"/>.</returns>
    public string GetConnectionString(
        string connectionName,
        bool enableMasterSlave = false,
        bool readOnly = false)
    {
        return _options.Value.PostgresConnectionString;
    }
}
