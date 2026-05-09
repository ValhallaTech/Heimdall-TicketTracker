using FluentAssertions;
using Heimdall.Web.Authorization.Policies;

namespace Heimdall.Web.Tests.Authorization.Policies;

public class OpenFgaRequirementTests
{
    [Fact]
    public void Constructor_Should_SetProperties_When_AllArgumentsValid()
    {
        var req = new OpenFgaRequirement("ticket", "view", "ticketId");

        req.ObjectType.Should().Be("ticket");
        req.Relation.Should().Be("view");
        req.RouteValueKey.Should().Be("ticketId");
    }

    [Fact]
    public void Constructor_Should_AllowNullRouteValueKey()
    {
        var req = new OpenFgaRequirement("ticket", "view", null);

        req.RouteValueKey.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Should_Throw_When_ObjectTypeIsBlank(string? objectType)
    {
        Action act = () => new OpenFgaRequirement(objectType!, "view", "ticketId");

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("objectType");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Should_Throw_When_RelationIsBlank(string? relation)
    {
        Action act = () => new OpenFgaRequirement("ticket", relation!, "ticketId");

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("relation");
    }
}
