using System;
using Heimdall.Core.Models;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Converts <see cref="TeamMemberRole"/> to and from its Postgres
/// <c>team_member_role</c> wire representation. The wire format is snake_case
/// (<c>manager</c>, <c>team_lead</c>, <c>member</c>, <c>viewer</c>) — different
/// from .NET's PascalCase enum names — so a stringly-typed converter is the
/// least-surprising contact point for both Dapper parameter binding (write
/// path, via <see cref="Dapper.DynamicParameters"/>) and DTO-based row
/// materialization (read path, in <see cref="TeamMemberRepository"/>).
/// </summary>
internal static class TeamMemberRoleConverter
{
    /// <summary>
    /// Returns the Postgres wire string for the given <see cref="TeamMemberRole"/>.
    /// </summary>
    /// <param name="role">The role to convert.</param>
    /// <returns>The snake_case wire string accepted by the <c>team_member_role</c> enum column.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is not a known enum member.</exception>
    public static string ToWireString(this TeamMemberRole role)
    {
        return role switch
        {
            TeamMemberRole.Manager => "manager",
            TeamMemberRole.TeamLead => "team_lead",
            TeamMemberRole.Member => "member",
            TeamMemberRole.Viewer => "viewer",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown TeamMemberRole value."
            ),
        };
    }

    /// <summary>
    /// Parses a Postgres wire string into a <see cref="TeamMemberRole"/>. Matching is
    /// case-insensitive (ordinal) so callers do not have to second-guess server casing.
    /// </summary>
    /// <param name="value">The wire string to parse.</param>
    /// <returns>The corresponding <see cref="TeamMemberRole"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not a known wire string.</exception>
    public static TeamMemberRole ParseWireString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(value, "manager", StringComparison.OrdinalIgnoreCase))
        {
            return TeamMemberRole.Manager;
        }

        if (string.Equals(value, "team_lead", StringComparison.OrdinalIgnoreCase))
        {
            return TeamMemberRole.TeamLead;
        }

        if (string.Equals(value, "member", StringComparison.OrdinalIgnoreCase))
        {
            return TeamMemberRole.Member;
        }

        if (string.Equals(value, "viewer", StringComparison.OrdinalIgnoreCase))
        {
            return TeamMemberRole.Viewer;
        }

        throw new ArgumentException(
            $"'{value}' is not a recognised team_member_role wire string.",
            nameof(value)
        );
    }
}

