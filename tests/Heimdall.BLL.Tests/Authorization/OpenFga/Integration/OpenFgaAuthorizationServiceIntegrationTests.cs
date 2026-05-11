using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Tests.Shared.OpenFga;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenFga.Sdk.Client;
using Xunit;

namespace Heimdall.BLL.Tests.Authorization.OpenFga.Integration;

/// <summary>
/// Phase 3.7 step 12 — adapter integration tests for
/// <see cref="OpenFgaAuthorizationService"/> against a real
/// <c>docker.io/openfga/openfga</c> sidecar provisioned by
/// <see cref="OpenFgaTestcontainersFixture"/>. Verifies the behaviours from
/// <c>docs/proposals/openfga.md</c> §3 step 6 (the policy adapter) against
/// the merged <c>authz/model.fga</c> model that the production app pins.
/// </summary>
/// <remarks>
/// <para>
/// Each test seeds a small set of tuples through the production
/// <see cref="OpenFgaTupleWriter"/> (so the seeding helper is itself testing
/// the write path under test), then exercises the read side.
/// </para>
/// <para>
/// Tuples are isolated per test by minting fresh organization / team /
/// project / ticket / user ids — the fixture's store is shared across the
/// collection, but the (user, relation, object) tuple space is partitioned
/// so independent tests cannot leak state into one another.
/// </para>
/// </remarks>
[Collection(OpenFgaIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OpenFgaAuthorizationServiceIntegrationTests
{
    private readonly OpenFgaClient _client;
    private readonly OpenFgaAuthorizationService _service;
    private readonly OpenFgaTupleWriter _writer;

    /// <summary>Initializes a new instance.</summary>
    public OpenFgaAuthorizationServiceIntegrationTests(OpenFgaTestcontainersFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _client = fixture.CreateSdkClient();

        OpenFgaOptions options = new()
        {
            ApiUrl = fixture.ApiUrl,
            StoreId = fixture.StoreId,
            AuthorizationModelId = fixture.AuthorizationModelId,
            PresharedKey = fixture.PresharedKey,

            // Sub-millisecond TTL keeps the cache effectively off for these
            // tests so we observe the sidecar's behaviour, not stale reads.
            CacheTtl = TimeSpan.FromMilliseconds(1),
        };
        IOptions<OpenFgaOptions> optionsAccessor = Options.Create(options);

        _service = new OpenFgaAuthorizationService(
            _client,
            new MemoryCache(new MemoryCacheOptions()),
            optionsAccessor,
            NullLogger<OpenFgaAuthorizationService>.Instance);

        // Audit writer is a no-op Mock — none of these tests exercise a write
        // failure path so the audit seam stays out of scope here.
        Mock<IAuditEventWriter> auditWriterMock = new(MockBehavior.Loose);
        _writer = new OpenFgaTupleWriter(
            _client,
            auditWriterMock.Object,
            NullLogger<OpenFgaTupleWriter>.Instance);
    }

    /// <summary>
    /// Org-admin must inherit <c>view</c> on a deeply-nested ticket via the
    /// <c>parent_project → parent_team → parent_org</c> walk declared in
    /// <c>authz/model.fga</c>.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_Check_GrantsViaParentInheritance()
    {
        // Arrange
        TestHierarchy hierarchy = await SeedHierarchyAsync().ConfigureAwait(false);

        // Act
        bool allowed = await _service
            .CheckAsync(
                new FgaCheckRequest(
                    TupleShapes.UserRef(hierarchy.OrgAdmin),
                    "view",
                    TupleShapes.TicketRef(hierarchy.TicketId),
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        allowed.Should().BeTrue(
            "org-admin must inherit view on every descendant ticket per authz/model.fga");
    }

    /// <summary>
    /// A user with no membership anywhere must be denied every relation on
    /// every object — the deny-closed non-member case from
    /// <c>authz/store.fga.yaml</c> proved against the live sidecar.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_Check_DeniesUnrelatedUser()
    {
        // Arrange
        TestHierarchy hierarchy = await SeedHierarchyAsync().ConfigureAwait(false);
        Guid stranger = Guid.NewGuid();

        // Act
        bool view = await _service
            .CheckAsync(
                new FgaCheckRequest(
                    TupleShapes.UserRef(stranger),
                    "view",
                    TupleShapes.TicketRef(hierarchy.TicketId),
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);
        bool edit = await _service
            .CheckAsync(
                new FgaCheckRequest(
                    TupleShapes.UserRef(stranger),
                    "edit",
                    TupleShapes.TicketRef(hierarchy.TicketId),
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        view.Should().BeFalse("non-member is deny-closed");
        edit.Should().BeFalse("non-member is deny-closed");
    }

    /// <summary>
    /// Reporter and assignee each get <c>view</c> / <c>comment</c> /
    /// <c>edit</c> / <c>assign</c> on their own ticket. A project-viewer can
    /// <c>view</c> but not <c>edit</c>.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_Check_ReporterAndAssigneeSelfGrants()
    {
        // Arrange
        TestHierarchy h = await SeedHierarchyAsync().ConfigureAwait(false);

        Guid projectViewer = Guid.NewGuid();
        await _writer
            .WriteAsync(TupleShapes.ProjectViewer(h.ProjectId, projectViewer), CancellationToken.None)
            .ConfigureAwait(false);

        // Act + Assert — reporter
        (await CheckTicketAsync(h.Reporter, "view", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();
        (await CheckTicketAsync(h.Reporter, "comment", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();
        (await CheckTicketAsync(h.Reporter, "edit", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();
        (await CheckTicketAsync(h.Reporter, "assign", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();

        // Assignee
        (await CheckTicketAsync(h.Assignee, "view", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();
        (await CheckTicketAsync(h.Assignee, "edit", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();

        // Project-viewer: view yes, edit no.
        (await CheckTicketAsync(projectViewer, "view", h.TicketId).ConfigureAwait(false))
            .Should().BeTrue();
        (await CheckTicketAsync(projectViewer, "edit", h.TicketId).ConfigureAwait(false))
            .Should().BeFalse("project#viewer is read-only — no edit grant");
    }

    /// <summary>
    /// <c>BatchCheck</c> must round-trip multiple <c>(user, relation, object)</c>
    /// tuples in a single call and preserve per-item ordering / allow-deny
    /// outcomes.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_BatchCheck_RoundTrip()
    {
        // Arrange
        TestHierarchy h = await SeedHierarchyAsync().ConfigureAwait(false);
        Guid stranger = Guid.NewGuid();
        IReadOnlyList<FgaCheckRequest> requests =
        [
            new(TupleShapes.UserRef(h.OrgAdmin), "view", TupleShapes.TicketRef(h.TicketId), FgaConsistency.HigherConsistency),
            new(TupleShapes.UserRef(stranger), "view", TupleShapes.TicketRef(h.TicketId), FgaConsistency.HigherConsistency),
            new(TupleShapes.UserRef(h.Reporter), "edit", TupleShapes.TicketRef(h.TicketId), FgaConsistency.HigherConsistency),
            new(TupleShapes.UserRef(stranger), "edit", TupleShapes.TicketRef(h.TicketId), FgaConsistency.HigherConsistency),
        ];

        // Act
        IReadOnlyList<bool> results = await _service
            .BatchCheckAsync(requests, CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        results.Should().HaveCount(4);
        results.Should().Equal(true, false, true, false);
    }

    /// <summary>
    /// <c>ListObjects</c> must return the set of tickets the user can view
    /// (the queue-page hot path) — and only that set.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_ListObjects_FiltersTicketsForUser()
    {
        // Arrange — two tickets in one project; a project-member sees both,
        // a stranger sees none.
        TestHierarchy h = await SeedHierarchyAsync().ConfigureAwait(false);
        int secondTicket = h.TicketId + 1;
        await _writer.WriteAsync(
            new[] { TupleShapes.TicketParentProject(secondTicket, h.ProjectId) },
            Array.Empty<TupleKey>(),
            CancellationToken.None).ConfigureAwait(false);

        Guid projectMember = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        await _writer
            .WriteAsync(TupleShapes.ProjectMember(h.ProjectId, projectMember), CancellationToken.None)
            .ConfigureAwait(false);

        // Act
        IReadOnlyList<string> memberObjects = await _service
            .ListObjectsAsync(
                new FgaListObjectsRequest(
                    TupleShapes.UserRef(projectMember),
                    "view",
                    TupleShapes.TicketType,
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<string> strangerObjects = await _service
            .ListObjectsAsync(
                new FgaListObjectsRequest(
                    TupleShapes.UserRef(stranger),
                    "view",
                    TupleShapes.TicketType,
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        memberObjects.Should().Contain(TupleShapes.TicketRef(h.TicketId));
        memberObjects.Should().Contain(TupleShapes.TicketRef(secondTicket));
        strangerObjects.Should().NotContain(TupleShapes.TicketRef(h.TicketId));
        strangerObjects.Should().NotContain(TupleShapes.TicketRef(secondTicket));
    }

    /// <summary>
    /// <c>ListUsers</c> returns the bare user ids that hold <c>view</c> on a
    /// ticket — the admin "who has access" surface — and must surface bare
    /// user ids only (no wildcards, no usersets), per the documented
    /// <see cref="IOpenFgaAuthorizationService.ListUsersAsync"/> contract.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_ListUsers_ResolvesViewersOfTicket()
    {
        // Arrange
        TestHierarchy h = await SeedHierarchyAsync().ConfigureAwait(false);

        // Act
        IReadOnlyList<string> users = await _service
            .ListUsersAsync(
                new FgaListUsersRequest(
                    TupleShapes.TicketType,
                    h.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "view",
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        users.Should().Contain(h.OrgAdmin.ToString("D"));
        users.Should().Contain(h.Reporter.ToString("D"));
        users.Should().Contain(h.Assignee.ToString("D"));
        users.Should().OnlyContain(
            u => u != null && u.Length == 36 && !u.Contains(':', StringComparison.Ordinal),
            "ListUsers must return bare user ids — no type prefix, no wildcards");
    }

    /// <summary>
    /// <c>Expand</c> returns a non-null userset tree for the queried
    /// <c>(object, relation)</c> pair. The precise tree shape is server-
    /// version-dependent and is covered structurally by the
    /// <see cref="OpenFgaAuthorizationService"/> unit tests; here we only
    /// assert the round-trip succeeds against a real sidecar.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_Expand_WalksUsersetTree()
    {
        // Arrange
        TestHierarchy h = await SeedHierarchyAsync().ConfigureAwait(false);

        // Act
        FgaExpandResult expand = await _service
            .ExpandAsync(
                new FgaExpandRequest(
                    TupleShapes.TicketType,
                    h.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "view"),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        expand.Should().NotBeNull();
        expand.Root.Should().NotBeNull(
            "the userset tree for ticket#view must include the reporter / assignee / parent_project walk");
    }

    /// <summary>
    /// The <c>consistency</c> parameter must reach the sidecar.
    /// <see cref="FgaConsistency.HigherConsistency"/> immediately after a
    /// write surfaces the freshly-written tuple. We do not assert on
    /// <see cref="FgaConsistency.MinimizeLatency"/> being stale — server-side
    /// cache flags make that observation flaky.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task OpenFgaAuthorizationService_Check_ConsistencyParameterIsPropagated()
    {
        // Arrange
        Guid orgId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        // Act — write, then immediately read with HIGHER_CONSISTENCY.
        await _writer
            .WriteAsync(TupleShapes.OrgAdmin(orgId, userId), CancellationToken.None)
            .ConfigureAwait(false);
        bool allowed = await _service
            .CheckAsync(
                new FgaCheckRequest(
                    TupleShapes.UserRef(userId),
                    "view",
                    TupleShapes.OrganizationRef(orgId),
                    FgaConsistency.HigherConsistency),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Assert
        allowed.Should().BeTrue(
            "HIGHER_CONSISTENCY read after write must observe the newly-written tuple");
    }

    private Task<bool> CheckTicketAsync(Guid user, string relation, int ticketId) =>
        _service.CheckAsync(
            new FgaCheckRequest(
                TupleShapes.UserRef(user),
                relation,
                TupleShapes.TicketRef(ticketId),
                FgaConsistency.HigherConsistency),
            CancellationToken.None);

    private async Task<TestHierarchy> SeedHierarchyAsync()
    {
        Guid orgId = Guid.NewGuid();
        Guid teamId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        int ticketId = Random.Shared.Next(100_000, int.MaxValue);
        Guid orgAdmin = Guid.NewGuid();
        Guid reporter = Guid.NewGuid();
        Guid assignee = Guid.NewGuid();

        TupleKey[] writes =
        [
            TupleShapes.TeamParentOrg(teamId, orgId),
            TupleShapes.ProjectParentTeam(projectId, teamId),
            TupleShapes.TicketParentProject(ticketId, projectId),
            TupleShapes.OrgAdmin(orgId, orgAdmin),
            TupleShapes.TicketReporter(ticketId, reporter),
            TupleShapes.TicketAssignee(ticketId, assignee),
        ];
        await _writer
            .WriteAsync(writes, Array.Empty<TupleKey>(), CancellationToken.None)
            .ConfigureAwait(false);

        return new TestHierarchy(orgId, teamId, projectId, ticketId, orgAdmin, reporter, assignee);
    }

    private sealed record TestHierarchy(
        Guid OrgId,
        Guid TeamId,
        Guid ProjectId,
        int TicketId,
        Guid OrgAdmin,
        Guid Reporter,
        Guid Assignee);
}
