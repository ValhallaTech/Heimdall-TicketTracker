using System.Data;

namespace Heimdall.Core.Interfaces;

/// <summary>
/// Factory for creating <see cref="IDbConnection"/> instances bound to the application's
/// configured database. Used by services that need to coordinate multiple writes inside a
/// single transaction (e.g. ticket route/assign + audit-event row, per
/// <c>docs/proposals/team-collaboration.md</c> §5.4) — the service opens the connection,
/// begins the transaction, and threads both into the participating repositories.
/// Implementations are expected to be stateless and safe to register as a singleton.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Creates a new, closed database connection. The caller is responsible for opening,
    /// using, and disposing the returned connection.
    /// </summary>
    /// <returns>A newly-constructed <see cref="IDbConnection"/>.</returns>
    IDbConnection CreateConnection();
}
