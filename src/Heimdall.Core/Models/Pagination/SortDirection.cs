namespace Heimdall.Core.Models.Pagination;

/// <summary>Sort direction for paginated queries.</summary>
public enum SortDirection
{
    /// <summary>Ascending order (A → Z, oldest → newest).</summary>
    Ascending,

    /// <summary>Descending order (Z → A, newest → oldest).</summary>
    Descending,
}
