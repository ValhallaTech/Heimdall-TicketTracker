using Dapper.Extensions;
using Dapper.Extensions.PostgreSql;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.DAL.Extensions;

/// <summary>
/// <see cref="IServiceCollection"/> extension methods that wire up the data-access layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Dapper.Extensions for PostgreSQL together with the
    /// <see cref="DataOptionsConnectionStringProvider"/> that sources the connection string
    /// from <see cref="DataOptions"/>. Call this from the application host once
    /// <see cref="DataOptions"/> has been configured.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // AddDapperForPostgreSQL registers IDapper (PostgreSqlDapper) as scoped and the default
        // IConnectionStringProvider as a singleton. AddDapperConnectionStringProvider then
        // replaces the default with our DataOptions-backed implementation so that the same
        // translated Npgsql connection string used elsewhere in the app is used here too.
        services.AddDapperForPostgreSQL();
        services.AddDapperConnectionStringProvider<DataOptionsConnectionStringProvider>();
        return services;
    }
}
