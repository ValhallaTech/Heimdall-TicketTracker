using System;
using System.Data;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Npgsql-backed <see cref="IDbConnectionFactory"/>. Sources the connection string from
/// bound <see cref="DataOptions"/> so the same translated Postgres connection string used
/// by the rest of the DAL is used here too. Stateless — safe to register as a singleton.
/// </summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlConnectionFactory"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public NpgsqlConnectionFactory(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);
}
