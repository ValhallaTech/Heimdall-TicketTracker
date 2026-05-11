using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Tests.Shared.OpenFga;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using Xunit;
using BllTupleKey = Heimdall.BLL.Authorization.OpenFga.TupleKey;

namespace Heimdall.BLL.Tests.Authorization.OpenFga.Integration;

/// <summary>
/// Phase 3.7 step 12 — integration tests for <see cref="OpenFgaTupleWriter"/>
/// against a real <c>docker.io/openfga/openfga</c> sidecar provisioned by
/// <see cref="OpenFgaTestcontainersFixture"/>. Verifies the
/// atomic-write / replace / idempotent-replay contract from
/// <c>docs/proposals/openfga.md</c> §3 step 7.
/// </summary>
/// <remarks>
/// Read-back assertions go through a direct SDK <c>Read</c> rather than the
/// adapter so a regression in <see cref="OpenFgaAuthorizationService"/> cannot
/// mask a write failure.
/// </remarks>
[Collection(OpenFgaIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OpenFgaTupleWriterIntegrationTests
{
    private readonly OpenFgaClient _client;
    private readonly OpenFgaTupleWriter _writer;
    private readonly Mock<IAuditEventWriter> _auditWriter;

    /// <summary>Initializes a new instance.</summary>
    public OpenFgaTupleWriterIntegrationTests(OpenFgaTestcontainersFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _client = fixture.CreateSdkClient();
        _auditWriter = new Mock<IAuditEventWriter>(MockBehavior.Loose);
        _writer = new OpenFgaTupleWriter(
            _client,
            _auditWriter.Object,
            NullLogger<OpenFgaTupleWriter>.Instance);
    }

    /// <summary>
    /// A single <see cref="ITupleWriter.WriteAsync(IReadOnlyList{TupleKey}, IReadOnlyList{TupleKey}, CancellationToken)"/>
    /// call carrying both writes and deletes commits atomically server-side
    /// — both effects are observable after one API call.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaTupleWriter_Write_IsAtomic_WritesAndDeletesInOneCall()
    {
        // Arrange
        Guid orgId = Guid.NewGuid();
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();

        // Pre-seed: user1 = admin.
        await _writer
            .WriteAsync(TupleShapes.OrgAdmin(orgId, user1), CancellationToken.None)
            .ConfigureAwait(false);

        // Act — atomic swap: delete user1, add user2.
        await _writer.WriteAsync(
            new[] { TupleShapes.OrgAdmin(orgId, user2) },
            new[] { TupleShapes.OrgAdmin(orgId, user1) },
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        (await TupleExistsAsync(TupleShapes.OrgAdmin(orgId, user2)).ConfigureAwait(false))
            .Should().BeTrue("the write half of the atomic call should be present");
        (await TupleExistsAsync(TupleShapes.OrgAdmin(orgId, user1)).ConfigureAwait(false))
            .Should().BeFalse("the delete half of the atomic call should be gone");
    }

    /// <summary>
    /// The <see cref="ITupleWriter.ReplaceAsync"/> convenience overload — used
    /// by the assignee-rotation hook in <c>TicketService</c> — must delete the
    /// previous tuple and write the new one in a single API call.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaTupleWriter_ReplaceAsync_AssigneeRotation()
    {
        // Arrange
        int ticketId = Random.Shared.Next(100_000, int.MaxValue);
        Guid oldAssignee = Guid.NewGuid();
        Guid newAssignee = Guid.NewGuid();
        await _writer
            .WriteAsync(TupleShapes.TicketAssignee(ticketId, oldAssignee), CancellationToken.None)
            .ConfigureAwait(false);

        // Act
        await _writer.ReplaceAsync(
            delete: TupleShapes.TicketAssignee(ticketId, oldAssignee),
            write: TupleShapes.TicketAssignee(ticketId, newAssignee),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        (await TupleExistsAsync(TupleShapes.TicketAssignee(ticketId, newAssignee)).ConfigureAwait(false))
            .Should().BeTrue();
        (await TupleExistsAsync(TupleShapes.TicketAssignee(ticketId, oldAssignee)).ConfigureAwait(false))
            .Should().BeFalse();
    }

    /// <summary>
    /// Re-writing an existing tuple must not throw. The merged production code
    /// catches the OpenFGA <c>write_failed_due_to_invalid_input</c> error and
    /// treats it as idempotent; <strong>no</strong>
    /// <c>openfga_tuple_write_failed</c> audit event is emitted.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaTupleWriter_Write_IdempotentReplay_NoFault()
    {
        // Arrange
        Guid orgId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        BllTupleKey tuple = TupleShapes.OrgAdmin(orgId, userId);
        await _writer.WriteAsync(tuple, CancellationToken.None).ConfigureAwait(false);

        // Act — replay must complete without throwing.
        Func<Task> replay = () => _writer.WriteAsync(tuple, CancellationToken.None);

        // Assert
        await replay.Should().NotThrowAsync().ConfigureAwait(false);
        _auditWriter.Verify(
            w => w.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "duplicate-tuple replay must not emit an openfga_tuple_write_failed audit event");
    }

    private async Task<bool> TupleExistsAsync(BllTupleKey tuple)
    {
        ClientReadRequest readReq = new()
        {
            User = tuple.User,
            Relation = tuple.Relation,
            Object = tuple.Object,
        };
        ReadResponse response = await _client
            .Read(readReq, options: null, CancellationToken.None)
            .ConfigureAwait(false);
        return (response.Tuples?.Count ?? 0) > 0;
    }
}
