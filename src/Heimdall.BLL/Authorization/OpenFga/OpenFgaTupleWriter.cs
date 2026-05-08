using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Core.Auditing;
using Microsoft.Extensions.Logging;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Default <see cref="ITupleWriter"/> wrapping <see cref="OpenFgaClient"/> per
/// <c>docs/proposals/openfga.md</c> §3 step 7. One <c>Write</c> API call per
/// invocation so writes + deletes commit atomically server-side.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Failure contract — direct-write option (a) per the proposal.</strong>
/// The relational write has already committed by the time this writer is called.
/// On failure we:
/// </para>
/// <list type="number">
///   <item><description>Log a warning with the failed payload shape (no PII beyond ids).</description></item>
///   <item><description>Increment <c>heimdall.openfga.tuple_write.errors</c>.</description></item>
///   <item><description>
///     Write an <c>openfga_tuple_write_failed</c> audit event with the serialized
///     <c>(writes, deletes)</c> payload so the backfill / reconciliation job can
///     replay drift detected by the Phase 3.4 step 8 sweep.
///   </description></item>
///   <item><description>Swallow the exception so the caller's happy path stays correct.</description></item>
/// </list>
/// <para>
/// Cancellation propagates as <see cref="OperationCanceledException"/> so cooperative
/// shutdown is honoured.
/// </para>
/// </remarks>
public sealed class OpenFgaTupleWriter : ITupleWriter
{
    private static readonly ActivitySource Source = new(OpenFgaAuthorizationService.ActivitySourceName);
    private static readonly Meter MeterInstance = new(OpenFgaAuthorizationService.MeterName);

    private static readonly Histogram<double> WriteLatency =
        MeterInstance.CreateHistogram<double>("heimdall.openfga.tuple_write.latency_ms");
    private static readonly Counter<long> WriteSuccess =
        MeterInstance.CreateCounter<long>("heimdall.openfga.tuple_write.success");
    private static readonly Counter<long> WriteErrors =
        MeterInstance.CreateCounter<long>("heimdall.openfga.tuple_write.errors");

    private static readonly JsonSerializerOptions AuditPayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly OpenFgaClient _client;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ILogger<OpenFgaTupleWriter> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="client">SDK client (singleton).</param>
    /// <param name="auditWriter">Audit-event writer used to record tuple-write failures.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">If any required dependency is <c>null</c>.</exception>
    public OpenFgaTupleWriter(
        OpenFgaClient client,
        IAuditEventWriter auditWriter,
        ILogger<OpenFgaTupleWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task WriteAsync(TupleKey single, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(single);
        return WriteAsync(new[] { single }, Array.Empty<TupleKey>(), cancellationToken);
    }

    /// <inheritdoc />
    public Task ReplaceAsync(TupleKey? delete, TupleKey? write, CancellationToken cancellationToken)
    {
        IReadOnlyList<TupleKey> writes = write is null
            ? Array.Empty<TupleKey>()
            : new[] { write };
        IReadOnlyList<TupleKey> deletes = delete is null
            ? Array.Empty<TupleKey>()
            : new[] { delete };

        if (writes.Count == 0 && deletes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(writes, deletes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyList<TupleKey> writes,
        IReadOnlyList<TupleKey> deletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletes);
        if (writes.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        using var activity = Source.StartActivity("openfga.tuple_write");
        activity?.SetTag("write_count", writes.Count);
        activity?.SetTag("delete_count", deletes.Count);

        long start = Stopwatch.GetTimestamp();
        try
        {
            ClientWriteRequest body = new(
                writes.Select(static t => new ClientTupleKey
                {
                    User = t.User,
                    Relation = t.Relation,
                    Object = t.Object,
                }).ToList(),
                deletes.Select(static t => new ClientTupleKeyWithoutCondition
                {
                    User = t.User,
                    Relation = t.Relation,
                    Object = t.Object,
                }).ToList());

            await _client.Write(body, options: null, cancellationToken).ConfigureAwait(false);
            WriteSuccess.Add(1);
            activity?.SetTag("outcome", "ok");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteErrors.Add(1);
            activity?.SetTag("outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await HandleWriteFailureAsync(writes, deletes, ex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteLatency.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private async Task HandleWriteFailureAsync(
        IReadOnlyList<TupleKey> writes,
        IReadOnlyList<TupleKey> deletes,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            exception,
            "OpenFGA tuple write failed; recording audit event for backfill reconciliation. writes={WriteCount} deletes={DeleteCount}",
            writes.Count,
            deletes.Count);

        try
        {
            FailedTupleWritePayload payload = new(
                writes.Select(static t => new TupleKeyDto(t.User, t.Relation, t.Object)).ToArray(),
                deletes.Select(static t => new TupleKeyDto(t.User, t.Relation, t.Object)).ToArray(),
                exception.GetType().Name,
                exception.Message);

            AuditEvent auditEvent = new()
            {
                EventType = "openfga_tuple_write_failed",
                PayloadJson = JsonSerializer.Serialize(payload, AuditPayloadJsonOptions),
            };

            await _auditWriter.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception auditEx)
        {
            // Audit writer itself failed — log only; never propagate (the caller's DB
            // write has already committed and the backfill job remains the last safety
            // net). This is intentional.
            _logger.LogError(
                auditEx,
                "Failed to record openfga_tuple_write_failed audit event; tuple drift will be reconciled by the backfill job only.");
        }
    }

    /// <summary>Audit-event payload for a failed tuple write.</summary>
    private sealed record FailedTupleWritePayload(
        TupleKeyDto[] Writes,
        TupleKeyDto[] Deletes,
        string ErrorType,
        string ErrorMessage);

    /// <summary>Audit-event DTO for a single tuple in the failure payload.</summary>
    private sealed record TupleKeyDto(string User, string Relation, string Object);
}
