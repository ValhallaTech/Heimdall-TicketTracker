using System;
using System.Security.Cryptography;
using Heimdall.Core.Tokens;

namespace Heimdall.BLL.Tokens;

/// <summary>
/// A request-scoped view of the current signing material: the <c>kid</c>, the JWA
/// algorithm, and a freshly-constructed <see cref="AsymmetricAlgorithm"/> carrying the
/// decrypted private key. Implements <see cref="IDisposable"/> so callers wrap it in a
/// <c>using</c> block — disposing the result disposes the underlying private key. This
/// is the explicit lifetime guarantee from
/// <c>docs/proposals/phase-5-signing-key-hardening.md</c> §2.1: the decrypted key MUST
/// NOT be cached, and disposal must run on every code path including exceptions.
/// </summary>
/// <remarks>
/// Do <strong>not</strong> store an instance in a field. Resolve it inside the method
/// that signs, sign, dispose. Two consecutive calls to
/// <see cref="ISigningKeyService.GetCurrentSigningCredentialsAsync"/> return reference-
/// distinct <see cref="Key"/> instances.
/// </remarks>
public sealed class SigningCredentialsResult : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SigningCredentialsResult"/> class.
    /// </summary>
    /// <param name="kid">The stable <c>kid</c> value to publish in the JWT header.</param>
    /// <param name="alg">The JWA algorithm corresponding to <paramref name="key"/>.</param>
    /// <param name="key">
    /// The decrypted private key; ownership transfers to this instance and is released
    /// on <see cref="Dispose"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="kid"/> is empty or whitespace.</exception>
    public SigningCredentialsResult(string kid, SigningAlgorithm alg, AsymmetricAlgorithm key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);
        ArgumentNullException.ThrowIfNull(key);
        Kid = kid;
        Alg = alg;
        Key = key;
    }

    /// <summary>Gets the stable key identifier to embed in the JWT header.</summary>
    public string Kid { get; }

    /// <summary>Gets the JWA algorithm corresponding to <see cref="Key"/>.</summary>
    public SigningAlgorithm Alg { get; }

    /// <summary>
    /// Gets the decrypted private key (an <see cref="RSA"/> or <see cref="ECDsa"/>
    /// instance). Owned by this result — do not dispose externally; do not retain a
    /// reference past disposal.
    /// </summary>
    public AsymmetricAlgorithm Key { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Key.Dispose();
    }
}
