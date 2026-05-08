using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Default <see cref="IOpenFgaAuthorizationService"/> wrapping <see cref="OpenFgaClient"/>
/// per <c>docs/proposals/openfga.md</c> §3 step 6. Adds:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Short-TTL in-process cache.</strong> Keyed on
///     <c>(StoreId | ModelId | user | relation | object | consistency)</c>; both
///     allow and deny outcomes are cached. The cache is process-wide and bounded
///     by <see cref="OpenFgaOptions.CacheTtl"/> — <strong>not</strong>
///     circuit-scoped per <c>docs/proposals/security-and-authorization.md</c> §9.2.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Deny-closed on outage.</strong> Transport / 5xx failures are logged
///     and translated into <c>false</c> (or empty list for <c>ListObjects</c>) so
///     an unreachable sidecar never silently allows access.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>OpenTelemetry instrumentation.</strong> One activity per call on
///     <see cref="ActivitySourceName"/>; histograms + counters on
///     <see cref="MeterName"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class OpenFgaAuthorizationService : IOpenFgaAuthorizationService
{
    /// <summary><see cref="ActivitySource"/> name (<c>Heimdall.OpenFga</c>).</summary>
    public const string ActivitySourceName = "Heimdall.OpenFga";

    /// <summary><see cref="Meter"/> name (<c>Heimdall.OpenFga</c>).</summary>
    public const string MeterName = "Heimdall.OpenFga";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter MeterInstance = new(MeterName);

    private static readonly Histogram<double> CheckLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.check.latency_ms");
    private static readonly Counter<long> CheckAllowed =
        MeterInstance.CreateCounter<long>("heimdall.openfga.check.allowed");
    private static readonly Counter<long> CheckDenied =
        MeterInstance.CreateCounter<long>("heimdall.openfga.check.denied");
    private static readonly Counter<long> CheckErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.check.errors");

    private static readonly Histogram<double> BatchCheckLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.batch_check.latency_ms");
    private static readonly Counter<long> BatchCheckAllowed =
        MeterInstance.CreateCounter<long>("heimdall.openfga.batch_check.allowed");
    private static readonly Counter<long> BatchCheckDenied =
        MeterInstance.CreateCounter<long>("heimdall.openfga.batch_check.denied");
    private static readonly Counter<long> BatchCheckErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.batch_check.errors");

    private static readonly Histogram<double> ListObjectsLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.list_objects.latency_ms");
    private static readonly Counter<long> ListObjectsResults =
        MeterInstance.CreateCounter<long>("heimdall.openfga.list_objects.results");
    private static readonly Counter<long> ListObjectsErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.list_objects.errors");

    private static readonly Histogram<double> ListUsersLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.list_users.latency_ms");
    private static readonly Counter<long> ListUsersResults =
        MeterInstance.CreateCounter<long>("heimdall.openfga.list_users.results");
    private static readonly Counter<long> ListUsersErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.list_users.errors");

    private static readonly Histogram<double> ExpandLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.expand.latency_ms");
    private static readonly Counter<long> ExpandErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.expand.errors");

    private readonly OpenFgaClient _client;
    private readonly IMemoryCache _cache;
    private readonly OpenFgaOptions _options;
    private readonly ILogger<OpenFgaAuthorizationService> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="client">SDK client, registered as a singleton by <c>AddHeimdallOpenFga</c>.</param>
    /// <param name="cache">Process-wide memory cache used for short-TTL check coalescing.</param>
    /// <param name="options">Bound <see cref="OpenFgaOptions"/>.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any required dependency is <c>null</c>.</exception>
    public OpenFgaAuthorizationService(
        OpenFgaClient client,
        IMemoryCache cache,
        IOptions<OpenFgaOptions> options,
        ILogger<OpenFgaAuthorizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> CheckAsync(FgaCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string cacheKey = BuildCacheKey(request);
        if (_cache.TryGetValue(cacheKey, out bool cached))
        {
            using var cachedActivity = StartCheckActivity(request, cacheHit: true);
            (cached ? CheckAllowed : CheckDenied).Add(1);
            cachedActivity?.SetTag("cache_hit", true);
            cachedActivity?.SetTag("outcome", cached ? "allow" : "deny");
            return cached;
        }

        using var activity = StartCheckActivity(request, cacheHit: false);
        long start = Stopwatch.GetTimestamp();
        try
        {
            CheckResponse response = await _client
                .Check(
                    new ClientCheckRequest
                    {
                        User = request.User,
                        Relation = request.Relation,
                        Object = request.Object,
                    },
                    new ClientCheckOptions { Consistency = MapConsistency(request.Consistency) },
                    cancellationToken)
                .ConfigureAwait(false);

            bool allowed = response.Allowed ?? false;
            _cache.Set(cacheKey, allowed, _options.CacheTtl);
            (allowed ? CheckAllowed : CheckDenied).Add(1);
            activity?.SetTag("outcome", allowed ? "allow" : "deny");
            return allowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CheckErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(
                ex,
                "OpenFGA Check failed; deny-closed. {User} #{Relation} {Object}",
                request.User,
                request.Relation,
                request.Object);
            return false;
        }
        finally
        {
            CheckLatency.Record(GetElapsedMs(start));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<bool>> BatchCheckAsync(
        IReadOnlyList<FgaCheckRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return Array.Empty<bool>();
        }

        bool[] results = new bool[requests.Count];
        List<int> uncachedIndices = new();
        List<ClientBatchCheckItem> uncached = new();

        for (int i = 0; i < requests.Count; i++)
        {
            FgaCheckRequest req = requests[i];
            string key = BuildCacheKey(req);
            if (_cache.TryGetValue(key, out bool cached))
            {
                results[i] = cached;
                continue;
            }

            uncachedIndices.Add(i);
            uncached.Add(new ClientBatchCheckItem
            {
                User = req.User,
                Relation = req.Relation,
                Object = req.Object,
                CorrelationId = FormattableString.Invariant($"{i}"),
            });
        }

        if (uncached.Count == 0)
        {
            return results;
        }

        using var activity = Source.StartActivity("openfga.batch_check");
        activity?.SetTag("count", uncached.Count);
        activity?.SetTag("cache_hit_count", requests.Count - uncached.Count);

        long start = Stopwatch.GetTimestamp();
        try
        {
            // Send a single batch — pick the consistency of the first uncached entry.
            // Heimdall only mixes consistencies at the call-boundary today; if that
            // changes the BLL caller is expected to split the batch itself.
            FgaConsistency consistency = requests[uncachedIndices[0]].Consistency;

            ClientBatchCheckResponse response = await _client
                .BatchCheck(
                    new ClientBatchCheckRequest { Checks = uncached },
                    new ClientBatchCheckOptions { Consistency = MapConsistency(consistency) },
                    cancellationToken)
                .ConfigureAwait(false);

            long allowedCount = 0;
            long deniedCount = 0;
            foreach (ClientBatchCheckSingleResponse item in response.Result ?? new())
            {
                if (!int.TryParse(
                        item.Request?.CorrelationId,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int idx))
                {
                    continue;
                }

                results[idx] = item.Allowed;
                if (item.Allowed)
                {
                    allowedCount++;
                }
                else
                {
                    deniedCount++;
                }

                _cache.Set(BuildCacheKey(requests[idx]), item.Allowed, _options.CacheTtl);
            }

            BatchCheckAllowed.Add(allowedCount);
            BatchCheckDenied.Add(deniedCount);
            activity?.SetTag("outcome", "ok");
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            BatchCheckErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(
                ex,
                "OpenFGA BatchCheck failed for {Count} entries; deny-closed.",
                uncached.Count);

            // Deny-closed: every uncached entry becomes false. Already-cached entries
            // keep their cached value (cached entries cannot fail).
            foreach (int idx in uncachedIndices)
            {
                results[idx] = false;
            }

            return results;
        }
        finally
        {
            BatchCheckLatency.Record(GetElapsedMs(start));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListObjectsAsync(
        FgaListObjectsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = Source.StartActivity("openfga.list_objects");
        activity?.SetTag("relation", request.Relation);
        activity?.SetTag("object_type", request.Type);
        activity?.SetTag("consistency", request.Consistency.ToString());

        long start = Stopwatch.GetTimestamp();
        try
        {
            ListObjectsResponse response = await _client
                .ListObjects(
                    new ClientListObjectsRequest
                    {
                        User = request.User,
                        Relation = request.Relation,
                        Type = request.Type,
                    },
                    new ClientListObjectsOptions { Consistency = MapConsistency(request.Consistency) },
                    cancellationToken)
                .ConfigureAwait(false);

            List<string> objects = response.Objects ?? new List<string>();
            ListObjectsResults.Add(objects.Count);
            activity?.SetTag("result_count", objects.Count);
            activity?.SetTag("outcome", "ok");
            return objects;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ListObjectsErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(
                ex,
                "OpenFGA ListObjects failed; returning empty. user={User} relation={Relation} type={Type}",
                request.User,
                request.Relation,
                request.Type);
            return Array.Empty<string>();
        }
        finally
        {
            ListObjectsLatency.Record(GetElapsedMs(start));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListUsersAsync(
        FgaListUsersRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = Source.StartActivity("openfga.list_users");
        activity?.SetTag("relation", request.Relation);
        activity?.SetTag("object_type", request.ObjectType);
        activity?.SetTag("consistency", request.Consistency.ToString());

        long start = Stopwatch.GetTimestamp();
        try
        {
            // Filter to user-typed subjects. The model bans `[user:*]` wildcards
            // (see authz/model.fga header) so a single-entry filter is correct;
            // we still defensively skip non-user subjects below.
            ListUsersResponse response = await _client
                .ListUsers(
                    new ClientListUsersRequest
                    {
                        Object = new FgaObject
                        {
                            Type = request.ObjectType,
                            Id = request.ObjectId,
                        },
                        Relation = request.Relation,
                        UserFilters = new List<UserTypeFilter>
                        {
                            new() { Type = TupleShapes.UserType },
                        },
                    },
                    new ClientListUsersOptions { Consistency = MapConsistency(request.Consistency) },
                    cancellationToken)
                .ConfigureAwait(false);

            List<string> userIds = new(response.Users?.Count ?? 0);
            foreach (User user in response.Users ?? new List<User>())
            {
                // OpenFGA's ListUsers result is a discriminated union of
                // (FgaObject, UsersetUser, TypedWildcard). We only surface bare
                // user ids — usersets and wildcards are intentionally dropped
                // so callers can resolve every returned id via IUserLookup
                // without a per-row parse step.
                FgaObject? obj = user.Object;
                if (obj is null || !string.Equals(obj.Type, TupleShapes.UserType, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(obj.Id))
                {
                    userIds.Add(obj.Id);
                }
            }

            ListUsersResults.Add(userIds.Count);
            activity?.SetTag("result_count", userIds.Count);
            activity?.SetTag("outcome", "ok");
            return userIds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ListUsersErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(
                ex,
                "OpenFGA ListUsers failed; returning empty. object_type={ObjectType} relation={Relation}",
                request.ObjectType,
                request.Relation);
            return Array.Empty<string>();
        }
        finally
        {
            ListUsersLatency.Record(GetElapsedMs(start));
        }
    }

    /// <inheritdoc />
    public async Task<FgaExpandResult> ExpandAsync(
        FgaExpandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = Source.StartActivity("openfga.expand");
        activity?.SetTag("relation", request.Relation);
        activity?.SetTag("object_type", request.ObjectType);

        long start = Stopwatch.GetTimestamp();
        try
        {
            // Expand is fixed at MinimizeLatency: the call is a sub-second tree
            // walk used by the admin "why" surface, never a read-after-write
            // hot path. Documenting here so a future change-of-mind has to
            // justify itself.
            ExpandResponse response = await _client
                .Expand(
                    new ClientExpandRequest
                    {
                        Relation = request.Relation,
                        Object = FormattableString.Invariant($"{request.ObjectType}:{request.ObjectId}"),
                    },
                    new ClientExpandOptions { Consistency = ConsistencyPreference.MINIMIZELATENCY },
                    cancellationToken)
                .ConfigureAwait(false);

            FgaExpandNode? root = response.Tree?.Root is null
                ? null
                : ConvertNode(response.Tree.Root);

            activity?.SetTag("outcome", "ok");
            return new FgaExpandResult(root);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExpandErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(
                ex,
                "OpenFGA Expand failed; returning empty tree. object_type={ObjectType} relation={Relation}",
                request.ObjectType,
                request.Relation);
            return new FgaExpandResult(null);
        }
        finally
        {
            ExpandLatency.Record(GetElapsedMs(start));
        }
    }

    /// <summary>
    /// Recursively projects an SDK <see cref="Node"/> onto our public POCO
    /// tree. <c>null</c>-safe at every level — a malformed server response
    /// degrades to a partially-populated tree rather than a NullReferenceException.
    /// </summary>
    private static FgaExpandNode ConvertNode(Node node)
    {
        FgaExpandLeaf? leaf = node.Leaf is null ? null : ConvertLeaf(node.Leaf);

        IReadOnlyList<FgaExpandNode>? union = node.Union?.VarNodes is { } unionNodes
            ? ConvertNodes(unionNodes)
            : null;

        IReadOnlyList<FgaExpandNode>? intersection = node.Intersection?.VarNodes is { } intersectionNodes
            ? ConvertNodes(intersectionNodes)
            : null;

        FgaExpandDifference? difference = null;
        if (node.Difference is { Base: { } baseNode, Subtract: { } subNode })
        {
            difference = new FgaExpandDifference(ConvertNode(baseNode), ConvertNode(subNode));
        }

        return new FgaExpandNode(
            node.Name ?? string.Empty,
            leaf,
            union,
            intersection,
            difference);
    }

    private static IReadOnlyList<FgaExpandNode> ConvertNodes(List<Node> nodes)
    {
        FgaExpandNode[] result = new FgaExpandNode[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            result[i] = ConvertNode(nodes[i]);
        }

        return result;
    }

    private static FgaExpandLeaf ConvertLeaf(Leaf leaf)
    {
        IReadOnlyList<string> users = Array.Empty<string>();
        if (leaf.Users?.VarUsers is { Count: > 0 } leafUsers)
        {
            users = leafUsers.AsReadOnly();
        }

        string? computed = leaf.Computed?.Userset;

        FgaExpandTupleToUserset? ttu = null;
        if (leaf.TupleToUserset is { } source)
        {
            IReadOnlyList<string> computedSets = source.Computed is null
                ? Array.Empty<string>()
                : source.Computed.ConvertAll(static c => c.Userset ?? string.Empty);
            ttu = new FgaExpandTupleToUserset(source.Tupleset ?? string.Empty, computedSets);
        }

        return new FgaExpandLeaf(users, computed, ttu);
    }

    private Activity? StartCheckActivity(FgaCheckRequest request, bool cacheHit)
    {
        Activity? activity = Source.StartActivity("openfga.check");
        if (activity is null)
        {
            return null;
        }

        // Intentionally omit the fully-qualified `user` and `object` ids: they are
        // high-cardinality (PII for `user:`, per-row id for objects) and would blow
        // up the tracing/metrics backend if exported as Activity tags. `object_type`
        // is the highest-cardinality dimension we expose.
        activity.SetTag("relation", request.Relation);
        activity.SetTag("object_type", ExtractObjectType(request.Object));
        activity.SetTag("consistency", request.Consistency.ToString());
        activity.SetTag("cache_hit", cacheHit);
        return activity;
    }

    private string BuildCacheKey(FgaCheckRequest request) =>
        FormattableString.Invariant(
            $"{_options.StoreId}|{_options.AuthorizationModelId}|{request.User}|{request.Relation}|{request.Object}|{request.Consistency}");

    private static ConsistencyPreference MapConsistency(FgaConsistency consistency) =>
        consistency switch
        {
            FgaConsistency.HigherConsistency => ConsistencyPreference.HIGHERCONSISTENCY,
            _ => ConsistencyPreference.MINIMIZELATENCY,
        };

    private static string ExtractObjectType(string objectRef)
    {
        if (string.IsNullOrEmpty(objectRef))
        {
            return string.Empty;
        }

        int sep = objectRef.IndexOf(':');
        return sep <= 0 ? objectRef : objectRef[..sep];
    }

    private static double GetElapsedMs(long start) =>
        Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
