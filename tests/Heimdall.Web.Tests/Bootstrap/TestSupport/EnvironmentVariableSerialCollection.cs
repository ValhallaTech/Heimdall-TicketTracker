namespace Heimdall.Web.Tests.Bootstrap.TestSupport;

/// <summary>
/// xUnit collection used to serialise tests that mutate process-wide environment
/// variables. Members of this collection do not run in parallel with each other.
/// </summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableSerialCollection
{
    public const string Name = "EnvironmentVariableSerial";
}
