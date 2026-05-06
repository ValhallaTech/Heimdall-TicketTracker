using FluentAssertions;
using Heimdall.BLL.Authorization;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Tests.Authorization;

/// <summary>
/// Tests for <see cref="NotImplementedPermissionService"/> — the placeholder
/// implementation bound when <c>Authorization:Provider == "OpenFga"</c> ahead of
/// Phase 3. Every public method must throw <see cref="NotImplementedException"/>
/// so a premature flip of the flag fails loudly at the first authorization call
/// rather than silently allowing or denying actions.
/// </summary>
public class NotImplementedPermissionServiceTests
{
    private static Ticket NewTicket() => new()
    {
        Id = 1,
        Title = "T",
        TeamId = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        ReporterId = Guid.NewGuid(),
    };

    [Fact]
    public async Task CanViewTeamQueueAsync_Should_Throw_NotImplementedException()
    {
        var sut = new NotImplementedPermissionService();
        Func<Task> act = () => sut.CanViewTeamQueueAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task CanRouteTicketAsync_Should_Throw_NotImplementedException()
    {
        var sut = new NotImplementedPermissionService();
        Func<Task> act = () => sut.CanRouteTicketAsync(Guid.NewGuid(), NewTicket(), Guid.NewGuid(), default);
        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task CanAssignTicketAsync_Should_Throw_NotImplementedException()
    {
        var sut = new NotImplementedPermissionService();
        Func<Task> act = () => sut.CanAssignTicketAsync(Guid.NewGuid(), NewTicket(), Guid.NewGuid(), default);
        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task CanManageTeamMembersAsync_Should_Throw_NotImplementedException()
    {
        var sut = new NotImplementedPermissionService();
        Func<Task> act = () => sut.CanManageTeamMembersAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        await act.Should().ThrowAsync<NotImplementedException>();
    }
}
