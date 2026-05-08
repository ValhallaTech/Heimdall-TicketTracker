using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Web.Tests.Bootstrap.TestSupport;

/// <summary>
/// IDisposable helper that snapshots and restores a set of environment
/// variables. Use only inside the <see cref="EnvironmentVariableSerialCollection"/>
/// xUnit collection so members do not run in parallel with each other.
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originals;

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _originals = overrides.Keys.ToDictionary(
            k => k,
            k => Environment.GetEnvironmentVariable(k));

        foreach (KeyValuePair<string, string?> kvp in overrides)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kvp in _originals)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
    }
}
