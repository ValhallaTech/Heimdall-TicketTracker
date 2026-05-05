using Dapper;
using Dapper.Extensions;
using Dapper.Extensions.PostgreSql;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Auditing;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
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

        // Register the bridge between TeamMemberRole and the Postgres
        // team_member_role enum before any Dapper queries are issued. AddDal is
        // the single per-process entry point for DAL wiring, so calling
        // SqlMapper.AddTypeHandler here is naturally idempotent at runtime; tests
        // that re-invoke AddDal in-process are tolerated because Dapper's handler
        // dictionary is keyed by Type and re-registration is a no-op overwrite.
        SqlMapper.AddTypeHandler(new TeamMemberRoleTypeHandler());

        // AddDapperForPostgreSQL registers IDapper (PostgreSqlDapper) as scoped and the default
        // IConnectionStringProvider as a singleton. AddDapperConnectionStringProvider then
        // replaces the default with our DataOptions-backed implementation so that the same
        // translated Npgsql connection string used elsewhere in the app is used here too.
        services.AddDapperForPostgreSQL();
        services.AddDapperConnectionStringProvider<DataOptionsConnectionStringProvider>();

        // Append-only audit writer (Phase 1 step 7 of
        // docs/proposals/security-and-authorization.md §9.3). Scoped to align with
        // request-bound Identity / DbContext-style services even though the writer
        // itself is stateless and could be a singleton.
        services.AddScoped<IAuditEventWriter, AuditEventWriter>();

        // Phase 2.1 collaboration-hierarchy repositories
        // (docs/proposals/team-collaboration.md §4 step 13). Scoped to match the
        // existing audit writer; each call opens its own NpgsqlConnection.
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<ITeamRepository, TeamRepository>();
        services.AddScoped<IProjectRepository, ProjectRepository>();

        // Phase 2.2 collaboration-membership repositories
        // (docs/proposals/team-collaboration.md §4 step 4). Same lifetime rationale
        // as the Phase 2.1 hierarchy block above.
        services.AddScoped<IOrganizationMemberRepository, OrganizationMemberRepository>();
        services.AddScoped<ITeamMemberRepository, TeamMemberRepository>();
        services.AddScoped<IProjectMemberRepository, ProjectMemberRepository>();
        return services;
    }
}
