using System;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Xunit;

namespace Heimdall.BLL.Tests.Tokens;

/// <summary>
/// Phase 5.3 step 7 / 5.4 step 10 — pins the contract of
/// <see cref="RefreshTokenHasher.ComputeHash(string)"/>: deterministic, lower-case,
/// 64-character SHA-256 hex digest. The equality-based <c>GetByHashAsync</c> lookup
/// and the <c>UNIQUE (token_hash)</c> constraint on <c>refresh_tokens</c> depend on
/// every property exercised here.
/// </summary>
public sealed class RefreshTokenHasherTests
{
    [Fact]
    public void Should_ReturnSameHash_When_SameInputHashedTwice()
    {
        // Arrange
        const string plaintext = "abc123";

        // Act
        string first = RefreshTokenHasher.ComputeHash(plaintext);
        string second = RefreshTokenHasher.ComputeHash(plaintext);

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void Should_ReturnLowerCaseHex_When_HashingArbitraryInput()
    {
        // Arrange
        const string plaintext = "XYZ-MixedCase-Plaintext-9001";

        // Act
        string hash = RefreshTokenHasher.ComputeHash(plaintext);

        // Assert
        hash.Should().MatchRegex("^[0-9a-f]+$");
        hash.Should().Be(hash.ToLowerInvariant());
    }

    [Fact]
    public void Should_Return64CharacterDigest_When_HashingAnyInput()
    {
        // Arrange
        const string plaintext = "the-quick-brown-fox";

        // Act
        string hash = RefreshTokenHasher.ComputeHash(plaintext);

        // Assert — SHA-256 → 32 bytes → 64 hex chars.
        hash.Should().HaveLength(64);
    }

    [Fact]
    public void Should_ReturnKnownDigest_When_HashingKnownInput()
    {
        // Arrange — RFC 6234 test vector: SHA-256("abc").
        const string plaintext = "abc";
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        // Act
        string hash = RefreshTokenHasher.ComputeHash(plaintext);

        // Assert
        hash.Should().Be(expected);
    }

    [Fact]
    public void Should_ReturnDistinctHashes_When_InputsDiffer()
    {
        // Arrange
        const string a = "refresh-token-one";
        const string b = "refresh-token-two";

        // Act
        string ha = RefreshTokenHasher.ComputeHash(a);
        string hb = RefreshTokenHasher.ComputeHash(b);

        // Assert
        ha.Should().NotBe(hb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Throw_When_PlaintextIsNullOrWhitespace(string? plaintext)
    {
        // Act
        Action act = () => RefreshTokenHasher.ComputeHash(plaintext!);

        // Assert — ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentException
        // (ArgumentNullException for null derives from ArgumentException).
        act.Should().Throw<ArgumentException>();
    }
}
