using FluentAssertions;
using Heimdall.DAL.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Heimdall.DAL.Tests.Caching;

public class RedisCacheServiceTests
{
    private sealed class Box
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private static (RedisCacheService Sut, Mock<IConnectionMultiplexer> Mux, Mock<IDatabase> Db) CreateSut()
    {
        var db = new Mock<IDatabase>(MockBehavior.Strict);
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(db.Object);

        var sut = new RedisCacheService(mux.Object, NullLogger<RedisCacheService>.Instance);
        return (sut, mux, db);
    }

    [Fact]
    public void Should_Throw_When_MultiplexerIsNull()
    {
        Action act = () => new RedisCacheService(null!, NullLogger<RedisCacheService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new RedisCacheService(Mock.Of<IConnectionMultiplexer>(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnDeserializedValue_When_GetAsyncHits()
    {
        var (sut, _, db) = CreateSut();
        var json = JsonConvert.SerializeObject(new Box { Id = 1, Name = "n" });
        db.Setup(d => d.StringGetAsync("k", CommandFlags.None)).ReturnsAsync(json);

        var result = await sut.GetAsync<Box>("k");

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Name.Should().Be("n");
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetAsyncMisses()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.StringGetAsync("k", CommandFlags.None)).ReturnsAsync(RedisValue.Null);

        var result = await sut.GetAsync<Box>("k");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetAsyncThrowsRedisException()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.StringGetAsync("k", CommandFlags.None)).ThrowsAsync(new RedisException("boom"));

        var result = await sut.GetAsync<Box>("k");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetAsyncReturnsInvalidJson()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.StringGetAsync("k", CommandFlags.None)).ReturnsAsync("not-json{");

        var result = await sut.GetAsync<Box>("k");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_SerializeAndStore_When_SetAsyncCalled()
    {
        var (sut, _, db) = CreateSut();
        RedisValue captured = default;
        TimeSpan? capturedTtl = null;
        db.Setup(d => d.StringSetAsync(
                "k",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.Always))
          .Callback<RedisKey, RedisValue, TimeSpan?, When>(
              (_, v, t, _) => { captured = v; capturedTtl = t; })
          .ReturnsAsync(true);

        await sut.SetAsync("k", new Box { Id = 7, Name = "n" }, TimeSpan.FromMinutes(2));

        capturedTtl.Should().Be(TimeSpan.FromMinutes(2));
        captured.HasValue.Should().BeTrue();
        var deserialized = JsonConvert.DeserializeObject<Box>((string)captured!);
        deserialized!.Id.Should().Be(7);
        deserialized.Name.Should().Be("n");
    }

    [Fact]
    public async Task Should_SwallowRedisException_When_SetAsyncFails()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.Always))
          .ThrowsAsync(new RedisException("boom"));

        Func<Task> act = () => sut.SetAsync("k", new Box());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Should_DeleteKey_When_RemoveAsyncCalled()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.KeyDeleteAsync("k", CommandFlags.None)).ReturnsAsync(true);

        await sut.RemoveAsync("k");

        db.Verify(d => d.KeyDeleteAsync("k", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task Should_SwallowRedisException_When_RemoveAsyncFails()
    {
        var (sut, _, db) = CreateSut();
        db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None))
          .ThrowsAsync(new RedisException("boom"));

        Func<Task> act = () => sut.RemoveAsync("k");
        await act.Should().NotThrowAsync();
    }
}
