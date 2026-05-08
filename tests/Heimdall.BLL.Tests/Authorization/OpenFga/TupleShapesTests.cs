using System;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="TupleShapes"/> covering the role-collapse rules in
/// <c>docs/proposals/openfga-input-contract.md</c> §3 plus the canonical id
/// rendering invariants (lowercase <c>Guid.ToString("D")</c>, invariant-culture
/// integer formatting).
/// </summary>
public class TupleShapesTests
{
    private static readonly Guid OrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UserId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    // ─── Reference / formatting helpers ───────────────────────────────────

    [Fact]
    public void UserRef_Should_RenderLowercaseGuid_When_InputHasUppercaseHex()
    {
        TupleShapes.UserRef(UserId).Should().Be("user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public void OrganizationRef_Should_RenderLowercaseGuid()
    {
        TupleShapes.OrganizationRef(OrgId).Should().Be("organization:11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void TeamRef_Should_RenderLowercaseGuid()
    {
        TupleShapes.TeamRef(TeamId).Should().Be("team:22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void ProjectRef_Should_RenderLowercaseGuid()
    {
        TupleShapes.ProjectRef(ProjectId).Should().Be("project:33333333-3333-3333-3333-333333333333");
    }

    [Theory]
    [InlineData(0, "ticket:0")]
    [InlineData(1, "ticket:1")]
    [InlineData(int.MaxValue, "ticket:2147483647")]
    public void TicketRef_Should_RenderInvariantInteger(int id, string expected)
    {
        TupleShapes.TicketRef(id).Should().Be(expected);
    }

    [Fact]
    public void TicketRef_Should_RenderInvariantCulture_When_AmbientCultureUsesNonLatinDigits()
    {
        // Arabic-Egypt formats negative numbers with non-ASCII separators in some
        // surfaces; assert invariant rendering by switching ambient culture.
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("ar-EG");
            TupleShapes.TicketRef(42).Should().Be("ticket:42");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prior;
        }
    }

    // ─── Constants are stable wire contract ───────────────────────────────

    [Fact]
    public void Constants_Should_MatchInputContractWireValues()
    {
        TupleShapes.UserType.Should().Be("user");
        TupleShapes.OrganizationType.Should().Be("organization");
        TupleShapes.TeamType.Should().Be("team");
        TupleShapes.ProjectType.Should().Be("project");
        TupleShapes.TicketType.Should().Be("ticket");

        TupleShapes.AdminRelation.Should().Be("admin");
        TupleShapes.MemberRelation.Should().Be("member");
        TupleShapes.ViewerRelation.Should().Be("viewer");
        TupleShapes.ParentOrgRelation.Should().Be("parent_org");
        TupleShapes.ParentTeamRelation.Should().Be("parent_team");
        TupleShapes.ParentProjectRelation.Should().Be("parent_project");
        TupleShapes.ReporterRelation.Should().Be("reporter");
        TupleShapes.AssigneeRelation.Should().Be("assignee");
    }

    // ─── Direct-construction helpers ──────────────────────────────────────

    [Fact]
    public void OrgAdmin_Should_BuildExpectedTuple()
    {
        TupleShapes.OrgAdmin(OrgId, UserId)
            .Should().Be(new TupleKey(
                "user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "admin",
                "organization:11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void OrgMember_Should_BuildExpectedTuple()
    {
        TupleShapes.OrgMember(OrgId, UserId)
            .Should().Be(new TupleKey(
                "user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "member",
                "organization:11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void TeamAdmin_Should_BuildExpectedTuple() =>
        TupleShapes.TeamAdmin(TeamId, UserId).Should().Be(new TupleKey(
            "user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "admin",
            "team:22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void TeamMember_Should_BuildExpectedTuple() =>
        TupleShapes.TeamMember(TeamId, UserId).Relation.Should().Be("member");

    [Fact]
    public void TeamViewer_Should_BuildExpectedTuple() =>
        TupleShapes.TeamViewer(TeamId, UserId).Relation.Should().Be("viewer");

    [Fact]
    public void ProjectAdmin_Should_BuildExpectedTuple() =>
        TupleShapes.ProjectAdmin(ProjectId, UserId).Relation.Should().Be("admin");

    [Fact]
    public void ProjectMember_Should_BuildExpectedTuple() =>
        TupleShapes.ProjectMember(ProjectId, UserId).Relation.Should().Be("member");

    [Fact]
    public void ProjectViewer_Should_BuildExpectedTuple() =>
        TupleShapes.ProjectViewer(ProjectId, UserId).Relation.Should().Be("viewer");

    // ─── Role collapse: organization ──────────────────────────────────────

    [Theory]
    [InlineData("owner", "admin")]
    [InlineData("OWNER", "admin")]
    [InlineData("Owner", "admin")]
    [InlineData("admin", "admin")]
    [InlineData("Admin", "admin")]
    [InlineData("member", "member")]
    [InlineData("viewer", "member")]
    [InlineData("VIEWER", "member")]
    public void OrgMemberFromRole_Should_CollapseToContractRelation(string role, string expectedRelation)
    {
        TupleShapes.OrgMemberFromRole(OrgId, UserId, role).Relation.Should().Be(expectedRelation);
    }

    [Fact]
    public void OrgMemberFromRole_Should_TargetTheGivenOrganization()
    {
        TupleShapes.OrgMemberFromRole(OrgId, UserId, "owner").Object
            .Should().Be("organization:11111111-1111-1111-1111-111111111111");
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("")]
    [InlineData("OWNER ")] // trailing whitespace is not normalized
    public void OrgMemberFromRole_Should_Throw_When_RoleNotRecognized(string role)
    {
        Action act = () => TupleShapes.OrgMemberFromRole(OrgId, UserId, role);
        act.Should().Throw<ArgumentException>().WithParameterName("role");
    }

    [Fact]
    public void OrgMemberFromRole_Should_Throw_When_RoleIsNull()
    {
        Action act = () => TupleShapes.OrgMemberFromRole(OrgId, UserId, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── Role collapse: team ──────────────────────────────────────────────

    [Theory]
    [InlineData(TeamMemberRole.Manager, "admin")]
    [InlineData(TeamMemberRole.TeamLead, "admin")]
    [InlineData(TeamMemberRole.Member, "member")]
    [InlineData(TeamMemberRole.Viewer, "viewer")]
    public void TeamAdminFromRole_Should_CollapseToContractRelation(TeamMemberRole role, string expectedRelation)
    {
        TupleShapes.TeamAdminFromRole(TeamId, UserId, role).Relation.Should().Be(expectedRelation);
    }

    [Fact]
    public void TeamAdminFromRole_Should_Throw_When_RoleIsUnknownEnumValue()
    {
        Action act = () => TupleShapes.TeamAdminFromRole(TeamId, UserId, (TeamMemberRole)999);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("role");
    }

    // ─── Role collapse: project ───────────────────────────────────────────

    [Theory]
    [InlineData("owner", "admin")]
    [InlineData("admin", "admin")]
    [InlineData("Member", "member")]
    [InlineData("viewer", "viewer")]
    public void ProjectMemberFromRole_Should_CollapseToContractRelation(string role, string expectedRelation)
    {
        TupleShapes.ProjectMemberFromRole(ProjectId, UserId, role).Relation
            .Should().Be(expectedRelation);
    }

    [Fact]
    public void ProjectMemberFromRole_Should_Throw_When_RoleIsNull()
    {
        Action act = () => TupleShapes.ProjectMemberFromRole(ProjectId, UserId, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("ADMINISTRATOR")]
    public void ProjectMemberFromRole_Should_Throw_When_RoleNotRecognized(string role)
    {
        Action act = () => TupleShapes.ProjectMemberFromRole(ProjectId, UserId, role);
        act.Should().Throw<ArgumentException>().WithParameterName("role");
    }

    // ─── Parent-of edges ──────────────────────────────────────────────────

    [Fact]
    public void TeamParentOrg_Should_PointFromOrganizationToTeam()
    {
        TupleShapes.TeamParentOrg(TeamId, OrgId).Should().Be(new TupleKey(
            "organization:11111111-1111-1111-1111-111111111111",
            "parent_org",
            "team:22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public void ProjectParentTeam_Should_PointFromTeamToProject()
    {
        TupleShapes.ProjectParentTeam(ProjectId, TeamId).Should().Be(new TupleKey(
            "team:22222222-2222-2222-2222-222222222222",
            "parent_team",
            "project:33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public void TicketParentProject_Should_PointFromProjectToTicket()
    {
        TupleShapes.TicketParentProject(42, ProjectId).Should().Be(new TupleKey(
            "project:33333333-3333-3333-3333-333333333333",
            "parent_project",
            "ticket:42"));
    }

    [Fact]
    public void TicketReporter_Should_BuildExpectedTuple()
    {
        TupleShapes.TicketReporter(99, UserId).Should().Be(new TupleKey(
            "user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "reporter",
            "ticket:99"));
    }

    [Fact]
    public void TicketAssignee_Should_BuildExpectedTuple()
    {
        TupleShapes.TicketAssignee(99, UserId).Should().Be(new TupleKey(
            "user:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "assignee",
            "ticket:99"));
    }
}
