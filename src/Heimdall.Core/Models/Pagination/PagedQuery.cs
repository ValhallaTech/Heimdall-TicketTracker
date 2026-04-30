using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Heimdall.Core.Models.Pagination;

/// <summary>
/// Describes a single page of a sorted, optionally filtered query against the ticket store.
/// </summary>
/// <remarks>
/// Always construct instances through <see cref="PagedQuery(int, int, string?, string, SortDirection)"/>
/// or call <see cref="Sanitized"/> on an existing instance to guarantee that all fields are
/// within their valid ranges before the query is forwarded to the data layer.
/// </remarks>
public sealed class PagedQuery
{
    /// <summary>
    /// Field names that are permitted as sort keys, mapped to their corresponding
    /// PostgreSQL column names.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> AllowedSortFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "title",
            ["Status"] = "status",
            ["Priority"] = "priority",
            ["Reporter"] = "reporter",
            ["Assignee"] = "assignee",
            ["DateCreated"] = "date_created",
            ["DateUpdated"] = "date_updated",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private const string DefaultSortField = "DateCreated";
    private const int MinPageSize = 1;
    private const int MaxPageSize = 100;

    /// <summary>Gets the 1-based page number.</summary>
    public int Page { get; }

    /// <summary>Gets the maximum number of items to return per page (1–100).</summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the optional free-text search applied against title, description, reporter,
    /// and assignee.
    /// </summary>
    public string? SearchText { get; }

    /// <summary>
    /// Gets the field name used to order results. Always contains a value from
    /// <see cref="AllowedSortFields"/>.
    /// </summary>
    public string SortField { get; }

    /// <summary>Gets the direction in which results are sorted.</summary>
    public SortDirection SortDirection { get; }

    /// <summary>
    /// Initializes a new <see cref="PagedQuery"/> with defaults applied:
    /// page 1, page size 25, sorted descending by date created, no search text.
    /// </summary>
    public PagedQuery()
        : this(page: 1, pageSize: 25, searchText: null, sortField: DefaultSortField, sortDirection: SortDirection.Descending)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="PagedQuery"/> with explicit values.
    /// Values outside their valid ranges are clamped or defaulted automatically.
    /// </summary>
    /// <param name="page">Requested 1-based page number; clamped to ≥ 1.</param>
    /// <param name="pageSize">Items per page; clamped to 1–100.</param>
    /// <param name="searchText">Optional free-text search term.</param>
    /// <param name="sortField">
    /// Sort field name; must be a key in <see cref="AllowedSortFields"/>.
    /// Defaults to <c>DateCreated</c> if the supplied value is unrecognised.
    /// </param>
    /// <param name="sortDirection">Sort direction.</param>
    public PagedQuery(
        int page,
        int pageSize,
        string? searchText,
        string sortField,
        SortDirection sortDirection)
    {
        Page = page < 1 ? 1 : page;
        PageSize = pageSize < MinPageSize ? MinPageSize
                 : pageSize > MaxPageSize ? MaxPageSize
                 : pageSize;
        SearchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        SortField = AllowedSortFields.ContainsKey(sortField ?? string.Empty)
            ? sortField!
            : DefaultSortField;
        SortDirection = sortDirection;
    }

    /// <summary>
    /// Returns a new <see cref="PagedQuery"/> that is guaranteed to have all fields
    /// within their valid ranges, using the current instance as the source.
    /// </summary>
    /// <returns>A sanitized copy of this query.</returns>
    public PagedQuery Sanitized() =>
        new(Page, PageSize, SearchText, SortField, SortDirection);
}
