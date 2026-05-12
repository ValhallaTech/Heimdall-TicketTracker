using System;

namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// Carries every database id produced by a single
/// <see cref="AuthzSeedingHelper.SeedAsync"/> call.
/// </summary>
/// <param name="OrganizationId">Id of the seeded organisation.</param>
/// <param name="TeamId">Id of the seeded team.</param>
/// <param name="ProjectId">Id of the seeded project.</param>
/// <param name="TicketId">Integer id of the seeded ticket.</param>
/// <param name="CreatorUserId">Id of the user who owns the hierarchy as admin.</param>
/// <param name="ReporterUserId">Id of the ticket reporter / org member.</param>
public sealed record SeedResult(
    Guid OrganizationId,
    Guid TeamId,
    Guid ProjectId,
    int TicketId,
    Guid CreatorUserId,
    Guid ReporterUserId);
