namespace Heimdall.Core.Caching;

/// <summary>
/// Well-known Redis cache key constants shared across the application layers.
/// Centralizing these strings here prevents key mismatches when multiple projects
/// need to reference the same cache entries (e.g. DAL seeding invalidating a key
/// written by BLL read-through logic).
/// </summary>
public static class CacheKeys
{
    /// <summary>Caches the full, unfiltered ticket list.</summary>
    public const string TicketList = "heimdall:tickets:all";
}
