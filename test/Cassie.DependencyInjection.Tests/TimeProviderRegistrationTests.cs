using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Rest;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests;

public sealed class TimeProviderRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldUseRegisteredClockGivenCassieServiceWhenDeadlineStarts(bool rest)
    {
        // Arrange
        InvalidOperationException clockFailure = new("The registered clock was called.");
        ServiceCollection services = new();
        _ = services.AddSingleton<TimeProvider>(new ThrowingClock(clockFailure));
        _ = services.AddCassieDataSource("Host=127.0.0.1;Port=1;Username=app;Pooling=false");
        _ = services.AddCassieRestClient(new Uri("http://127.0.0.1:1/"));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var token = TestContext.Current.CancellationToken;

        // Act
        var failure = await Record.ExceptionAsync(() => rest
            ? scope.ServiceProvider.GetRequiredService<CassieRestClient>().ListCollectionsAsync(token)
            : scope.ServiceProvider.GetRequiredService<CassieConnection>().OpenAsync(token));

        // Assert
        Assert.Same(clockFailure, failure);
    }

    private sealed class ThrowingClock(Exception failure) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            throw failure;
        }
    }
}
