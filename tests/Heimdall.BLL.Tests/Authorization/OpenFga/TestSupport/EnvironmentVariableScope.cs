using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;

/// <summary>
/// IDisposable helper that snapshots a set of environment variables, allows the
/// caller to set them for the lifetime of the test, and restores the originals
/// on <see cref="Dispose"/>. Use only in tests inside the
/// <c>EnvironmentVariableSerial</c> xUnit collection so they do not run in
/// parallel with each other.
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
