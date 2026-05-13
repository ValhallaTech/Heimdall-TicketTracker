using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="SigningKeyService"/>. The real
/// <see cref="EphemeralDataProtectionProvider"/> is used so the
/// protect/unprotect round-trip and the tampered-ciphertext path are exercised
/// against a genuine envelope rather than a brittle one-way mock. Mocks are
/// <see cref="MockBehavior.Strict"/> per repo convention.
/// </summary>
public class SigningKeyServiceTests
{
    private readonly Mock<ISigningKeyRepository> _repo = new(MockBehavior.Strict);
    private readonly Mock<IJwksCacheInvalidator> _jwksInvalidator = new(MockBehavior.Strict);
    private readonly EphemeralDataProtectionProvider _dpProvider = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly RecordingLogger<SigningKeyService> _logger = new();
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2030-01-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly TokenOptions _options = new();

    private SigningKeyService CreateSut() => new(
        _repo.Object,
        _dpProvider,
        _cache,
        _jwksInvalidator.Object,
        Options.Create(_options),
        _logger,
        _time);

    [Fact]
    public async Task GenerateAsync_inserts_row_with_protected_pkcs8_for_rsa()
    {
        byte[]? capturedCiphertext = null;
        string? capturedKid = null;
        string? capturedAlg = null;
        DateTime capturedNotBefore = default;
        DateTime capturedNotAfter = default;

        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SigningKeyRecord?)null);
        _repo.Setup(r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, byte[], DateTime, DateTime, CancellationToken>(
                (kid, alg, _, ciphertext, nb, na, _) =>
                {
                    capturedKid = kid;
                    capturedAlg = alg;
                    capturedCiphertext = ciphertext;
                    capturedNotBefore = nb;
                    capturedNotAfter = na;
                })
            .Returns(Task.CompletedTask);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();

        string kid = await sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));

        kid.Should().Be(capturedKid);
        capturedKid.Should().MatchRegex("^[0-9a-f]{32}$", "kid must be 32 lowercase hex chars (Guid 'N')");
        capturedAlg.Should().Be("RS256");
        (capturedNotAfter - capturedNotBefore).Should().BeCloseTo(TimeSpan.FromDays(90), TimeSpan.FromSeconds(1));

        capturedCiphertext.Should().NotBeNull();

        // The captured payload must be ciphertext, not raw PKCS#8: importing it as PKCS#8
        // directly must fail, but unprotecting first must yield a valid PKCS#8 RSA key.
        using (var rsa = RSA.Create())
        {
            Action importRaw = () => rsa.ImportPkcs8PrivateKey(capturedCiphertext, out _);
            importRaw.Should().Throw<CryptographicException>("captured bytes must be the DP envelope, never plaintext");
        }

        byte[] plaintext = _dpProvider.CreateProtector("Heimdall.JwtSigningKeys.v1").Unprotect(capturedCiphertext!);
        using (var rsa = RSA.Create())
        {
            Action importPlain = () => rsa.ImportPkcs8PrivateKey(plaintext, out _);
            importPlain.Should().NotThrow("unprotected bytes must be valid PKCS#8");
        }

        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_inserts_row_with_protected_pkcs8_for_ecdsa()
    {
        byte[]? capturedCiphertext = null;
        string? capturedAlg = null;

        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SigningKeyRecord?)null);
        _repo.Setup(r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, byte[], DateTime, DateTime, CancellationToken>(
                (_, alg, _, ct, _, _, _) => { capturedAlg = alg; capturedCiphertext = ct; })
            .Returns(Task.CompletedTask);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();

        await sut.GenerateAsync(SigningAlgorithm.Es256, TimeSpan.FromDays(90));

        capturedAlg.Should().Be("ES256");
        capturedCiphertext.Should().NotBeNull();

        byte[] plaintext = _dpProvider.CreateProtector("Heimdall.JwtSigningKeys.v1").Unprotect(capturedCiphertext!);
        using var ecdsa = ECDsa.Create();
        Action importPlain = () => ecdsa.ImportPkcs8PrivateKey(plaintext, out _);
        importPlain.Should().NotThrow();

        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_rejects_when_overlap_window_smaller_than_access_token_lifetime()
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        var current = MakeRecord(notBefore: now - TimeSpan.FromHours(1), notAfter: now + TimeSpan.FromMinutes(5));
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);

        var sut = CreateSut();

        Func<Task> act = () => sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("00:05:00");
        ex.Which.Message.Should().Contain("00:15:00");

        _repo.Verify(
            r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_allowed_when_overlap_window_equals_access_token_lifetime()
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;

        // Exactly 15 minutes — the boundary. The rejection branch is `overlap < required`,
        // so equality MUST succeed. Pins the inclusive boundary so a future `<=` would fail.
        var current = MakeRecord(notBefore: now - TimeSpan.FromHours(1), notAfter: now + TimeSpan.FromMinutes(15));
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);
        _repo.Setup(r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();

        Func<Task> act = () => sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));

        await act.Should().NotThrowAsync();
        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_allowed_when_no_current_key_exists()
    {
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SigningKeyRecord?)null);
        _repo.Setup(r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();

        Func<Task> act = () => sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RetireAsync_calls_repo_and_invalidates_cache()
    {
        DateTime expected = _time.GetUtcNow().UtcDateTime;
        _repo.Setup(r => r.UpdateRetiredAtAsync("abc123", expected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();

        await sut.RetireAsync("abc123");

        _repo.Verify(r => r.UpdateRetiredAtAsync("abc123", expected, It.IsAny<CancellationToken>()), Times.Once);
        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task RetireAsync_does_not_invalidate_when_repo_reports_zero_rows_affected()
    {
        _repo.Setup(r => r.UpdateRetiredAtAsync("absent", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sut = CreateSut();

        await sut.RetireAsync("absent");

        _jwksInvalidator.Verify(j => j.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task GetCurrentSigningCredentialsAsync_returns_fresh_algorithm_per_call()
    {
        var (kid, ciphertext) = SeedRealRsa();
        var record = MakeRecord(kid: kid, alg: SigningAlgorithm.Rs256);

        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _repo.Setup(r => r.ReadPrivateKeyProtectedAsync(kid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciphertext);

        var sut = CreateSut();

        using var first = await sut.GetCurrentSigningCredentialsAsync();
        using var second = await sut.GetCurrentSigningCredentialsAsync();

        first.Kid.Should().Be(kid);
        first.Alg.Should().Be(SigningAlgorithm.Rs256);
        first.Key.Should().BeAssignableTo<RSA>();
        second.Key.Should().BeAssignableTo<RSA>();
        ReferenceEquals(first.Key, second.Key).Should().BeFalse("the decrypted key MUST never be cached (hardening §2.1)");
    }

    [Fact]
    public async Task GetCurrentSigningCredentialsAsync_throws_CryptographicException_on_tampered_ciphertext()
    {
        var (kid, ciphertext) = SeedRealRsa();

        // Tamper with the last byte — DP authenticates the envelope so unprotect must fail.
        ciphertext[^1] ^= 0xFF;

        var record = MakeRecord(kid: kid, alg: SigningAlgorithm.Rs256);
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _repo.Setup(r => r.ReadPrivateKeyProtectedAsync(kid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciphertext);

        var sut = CreateSut();

        Func<Task> act = () => sut.GetCurrentSigningCredentialsAsync();

        await act.Should().ThrowAsync<CryptographicException>();
        _logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error && e.Message.Contains(kid, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCurrentSigningCredentialsAsync_throws_when_no_active_key()
    {
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SigningKeyRecord?)null);

        var sut = CreateSut();

        Func<Task> act = () => sut.GetCurrentSigningCredentialsAsync();

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("No active signing key");
    }

    [Fact]
    public async Task GenerateAsync_logs_kid_at_information_but_never_logs_key_material()
    {
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SigningKeyRecord?)null);

        byte[]? capturedCiphertext = null;
        _repo.Setup(r => r.InsertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, byte[], DateTime, DateTime, CancellationToken>(
                (_, _, _, ct, _, _, _) => capturedCiphertext = ct)
            .Returns(Task.CompletedTask);
        _jwksInvalidator.Setup(j => j.Invalidate());

        var sut = CreateSut();
        string kid = await sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));

        _logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains(kid, StringComparison.Ordinal));

        string ciphertextB64 = Convert.ToBase64String(capturedCiphertext!);
        string prefix = ciphertextB64.Substring(0, Math.Min(16, ciphertextB64.Length));
        foreach (var entry in _logger.Entries)
        {
            entry.Message.Should().NotContain("BEGIN PRIVATE");
            entry.Message.Should().NotContain("BEGIN RSA");
            entry.Message.Should().NotContain(prefix);
        }
    }

    [Fact]
    public async Task GetCurrentSigningKeyAsync_caches_repository_lookups()
    {
        var record = MakeRecord();
        _repo.Setup(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var sut = CreateSut();

        var first = await sut.GetCurrentSigningKeyAsync();
        var second = await sut.GetCurrentSigningKeyAsync();

        first.Should().BeSameAs(record);
        second.Should().BeSameAs(record);
        _repo.Verify(r => r.GetCurrentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTrustedKeysAsync_caches_repository_lookups()
    {
        IReadOnlyList<SigningKeyRecord> rows = new[] { MakeRecord() };
        _repo.Setup(r => r.GetTrustedAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

        var sut = CreateSut();

        var a = await sut.GetTrustedKeysAsync();
        var b = await sut.GetTrustedKeysAsync();

        a.Should().BeSameAs(rows);
        b.Should().BeSameAs(rows);
        _repo.Verify(r => r.GetTrustedAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GenerateAsync_rejects_non_positive_validity(int seconds)
    {
        var sut = CreateSut();

        Func<Task> act = () => sut.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromSeconds(seconds));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_throws_on_null_dependencies()
    {
        var dp = _dpProvider;
        var opts = Options.Create(new TokenOptions());
        var logger = NullLoggerOf<SigningKeyService>();

        Action a = () => new SigningKeyService(null!, dp, _cache, _jwksInvalidator.Object, opts, logger);
        Action b = () => new SigningKeyService(_repo.Object, null!, _cache, _jwksInvalidator.Object, opts, logger);
        Action c = () => new SigningKeyService(_repo.Object, dp, null!, _jwksInvalidator.Object, opts, logger);
        Action d = () => new SigningKeyService(_repo.Object, dp, _cache, null!, opts, logger);
        Action e = () => new SigningKeyService(_repo.Object, dp, _cache, _jwksInvalidator.Object, null!, logger);
        Action f = () => new SigningKeyService(_repo.Object, dp, _cache, _jwksInvalidator.Object, opts, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
        d.Should().Throw<ArgumentNullException>();
        e.Should().Throw<ArgumentNullException>();
        f.Should().Throw<ArgumentNullException>();
    }

    private (string Kid, byte[] Ciphertext) SeedRealRsa()
    {
        string kid = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(2048);
        byte[] pkcs8 = rsa.ExportPkcs8PrivateKey();
        byte[] ciphertext = _dpProvider.CreateProtector("Heimdall.JwtSigningKeys.v1").Protect(pkcs8);
        return (kid, ciphertext);
    }

    private SigningKeyRecord MakeRecord(
        string? kid = null,
        SigningAlgorithm alg = SigningAlgorithm.Rs256,
        DateTime? notBefore = null,
        DateTime? notAfter = null)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        using var rsa = RSA.Create(2048);
        kid ??= Guid.NewGuid().ToString("N");
        return new SigningKeyRecord(
            Kid: kid,
            Alg: alg,
            PublicJwk: PublicJwk.FromRsa(rsa, kid, alg.ToJwaName()),
            NotBefore: notBefore ?? now,
            NotAfter: notAfter ?? now + TimeSpan.FromDays(90),
            RetiredAt: null,
            CreatedAt: now);
    }

    private static ILogger<T> NullLoggerOf<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}

/// <summary>
/// Minimal in-test <see cref="TimeProvider"/> with a settable now. Replaces the
/// <c>FakeTimeProvider</c> package which is not referenced by the test project.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>
/// Tiny <see cref="ILogger{T}"/> that records every emitted entry's level and
/// formatted message. Strict-mode mocks aren't a good fit for <c>ILogger</c>
/// because the .NET <c>LogInformation</c> / <c>LogError</c> extension methods
/// route through a single <c>Log&lt;TState&gt;</c> method with a synthesised state,
/// which is awkward to match.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = new();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
