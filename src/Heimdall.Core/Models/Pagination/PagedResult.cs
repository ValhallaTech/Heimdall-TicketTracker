using System;
using System.Collections.Generic;

namespace Heimdall.Core.Models.Pagination;

/// <summary>
/// Wraps a single page of query results together with the metadata required for
/// the caller to build pagination controls.
/// </summary>
/// <typeparam name="T">The type of each item in the page.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Gets or sets the items on the current page.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Gets or sets the total number of records that match the query (across all pages).</summary>
    public int TotalCount { get; init; }

    /// <summary>Gets or sets the 1-based page number returned.</summary>
    public int Page { get; init; }

    /// <summary>Gets or sets the maximum number of items per page.</summary>
    public int PageSize { get; init; }

    /// <summary>
    /// Gets the total number of pages required to expose all matching records at the
    /// current <see cref="PageSize"/>.
    /// </summary>
    public int TotalPages =>
        PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}
