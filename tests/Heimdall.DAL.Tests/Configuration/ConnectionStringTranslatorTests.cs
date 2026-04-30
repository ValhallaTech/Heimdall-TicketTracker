using FluentAssertions;
using Heimdall.DAL.Configuration;
using Npgsql;

namespace Heimdall.DAL.Tests.Configuration;

public class ConnectionStringTranslatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_ReturnNull_When_PostgresInputIsNullOrEmpty(string? input)
    {
        ConnectionStringTranslator.ToNpgsqlConnectionString(input).Should().BeNull();
    }

    [Theory]
    [InlineData("Host=localhost;Database=heimdall;Username=u;Password=p")]
    [InlineData("Server=db.example;Port=5433;Database=h;User ID=u;Password=p")]
    public void Should_PassThrough_When_NotAUrl(string input)
    {
        ConnectionStringTranslator.ToNpgsqlConnectionString(input).Should().Be(input);
    }

    [Fact]
    public void Should_TranslatePostgresUrl_When_StandardForm()
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            "postgres://alice:secret@db.example.com:5433/heimdall");

        result.Should().NotBeNull();
        var b = new NpgsqlConnectionStringBuilder(result);
        b.Host.Should().Be("db.example.com");
        b.Port.Should().Be(5433);
        b.Username.Should().Be("alice");
        b.Password.Should().Be("secret");
        b.Database.Should().Be("heimdall");
        b.ApplicationName.Should().Be("Heimdall.Web");
        b.SslMode.Should().Be(SslMode.Prefer);
    }

    [Fact]
    public void Should_DefaultPortTo5432_When_PortMissing()
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            "postgresql://u:p@h.example/db");

        var b = new NpgsqlConnectionStringBuilder(result);
        b.Port.Should().Be(5432);
    }

    [Fact]
    public void Should_UrlDecodeUserAndPassword_When_PercentEncoded()
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            "postgres://us%40er:p%40ss%21@h:5432/db");

        var b = new NpgsqlConnectionStringBuilder(result);
        b.Username.Should().Be("us@er");
        b.Password.Should().Be("p@ss!");
    }

    [Theory]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("nonsense", SslMode.Prefer)]
    public void Should_MapSslModeQueryParameter_When_Present(string ssl, SslMode expected)
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            $"postgres://u:p@h:5432/db?sslmode={ssl}");

        var b = new NpgsqlConnectionStringBuilder(result);
        b.SslMode.Should().Be(expected);
    }

    [Fact]
    public void Should_IgnoreSslRootCert_When_PathDoesNotExist()
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            "postgres://u:p@h/db?sslrootcert=/path/that/does/not/exist.pem");

        var b = new NpgsqlConnectionStringBuilder(result);
        b.RootCertificate.Should().BeNull();
    }

    [Fact]
    public void Should_AcceptSslRootCert_When_PathExists()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var encoded = Uri.EscapeDataString(tempFile);
            var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
                $"postgres://u:p@h/db?sslrootcert={encoded}");

            var b = new NpgsqlConnectionStringBuilder(result);
            b.RootCertificate.Should().Be(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Should_HandlePasswordlessUserInfo_When_NoPassword()
    {
        var result = ConnectionStringTranslator.ToNpgsqlConnectionString(
            "postgres://onlyuser@h/db");

        result.Should().NotBeNull();
        var b = new NpgsqlConnectionStringBuilder(result);
        b.Username.Should().Be("onlyuser");
        b.Password.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_ReturnNull_When_RedisInputIsNullOrEmpty(string? input)
    {
        ConnectionStringTranslator.ToRedisConfiguration(input).Should().BeNull();
    }

    [Theory]
    [InlineData("redis-host:6380")]
    [InlineData("host1:6379,host2:6379")]
    public void Should_PassThroughRedis_When_NotAUrl(string input)
    {
        ConnectionStringTranslator.ToRedisConfiguration(input).Should().Be(input);
    }

    [Fact]
    public void Should_TranslateRedisUrl_When_StandardForm()
    {
        var result = ConnectionStringTranslator.ToRedisConfiguration(
            "redis://:secret@cache.example:6380");

        result.Should().Be("cache.example:6380,password=secret");
    }

    [Fact]
    public void Should_DefaultRedisPort_When_PortMissing()
    {
        var result = ConnectionStringTranslator.ToRedisConfiguration("redis://cache.example");

        result.Should().Be("cache.example:6379");
    }

    [Fact]
    public void Should_AddSsl_When_RedissScheme()
    {
        var result = ConnectionStringTranslator.ToRedisConfiguration(
            "rediss://:s%40cret@cache.example:6380");

        result.Should().Be("cache.example:6380,password=s@cret,ssl=true");
    }

    [Fact]
    public void Should_HandleUserOnlyUserInfo_When_NoColon()
    {
        var result = ConnectionStringTranslator.ToRedisConfiguration(
            "redis://onlypass@cache.example:6379");

        result.Should().Be("cache.example:6379,password=onlypass");
    }
}
