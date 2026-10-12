using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using OrderFlow;
using Xunit;
namespace OrderFlow.Tests;

public class OrderTests
{
    [Fact]
    public async Task ConcurrentRetriesQueueExactlyOneOrder()
    {
        var store = new OrderStore(TimeProvider.System); var session = Guid.NewGuid(); var key = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.Submit(session, key, Scenario.Recover))));
        Assert.Single(results, x => !x.Replayed); Assert.Single(results.Select(x => x.Order!.Id).Distinct());
        Assert.True(store.Reader.TryRead(out _)); Assert.False(store.Reader.TryRead(out _));
    }
    [Fact]
    public void ConflictingKeyIsRejected()
    {
        var store = new OrderStore(TimeProvider.System); var session = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Submit(session, key, Scenario.Success);
        Assert.NotNull(store.Submit(session, key, Scenario.Exhaust).Error); Assert.Single(store.List(session));
    }
    [Fact]
    public void SessionsCannotReadOrMutateOtherOrders()
    {
        var store = new OrderStore(TimeProvider.System); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var order = store.Submit(a, Guid.NewGuid(), Scenario.Success).Order!;
        Assert.Empty(store.List(b)); Assert.Null(store.Advance(new(b, order.Id), OrderStatus.Processing, 1, "Not allowed"));
        Assert.Equal(OrderStatus.Queued, Assert.Single(store.List(a)).Status);
    }
    [Fact]
    public void CapacityIsBoundedAndReplayStillWorks()
    {
        var store = new OrderStore(TimeProvider.System, capacity: 2, perSession: 1); var session = Guid.NewGuid(); var key = Guid.NewGuid();
        store.Submit(session, key, Scenario.Success);
        Assert.NotNull(store.Submit(session, Guid.NewGuid(), Scenario.Success).Error); Assert.True(store.Submit(session, key, Scenario.Success).Replayed);
        Assert.Null(store.Submit(Guid.NewGuid(), Guid.NewGuid(), Scenario.Success).Error);
        Assert.NotNull(store.Submit(Guid.NewGuid(), Guid.NewGuid(), Scenario.Success).Error);
    }
    [Theory]
    [InlineData(Scenario.Success, OrderStatus.Completed, 1)]
    [InlineData(Scenario.Recover, OrderStatus.Completed, 3)]
    [InlineData(Scenario.Exhaust, OrderStatus.Failed, 3)]
    public async Task ProcessorResolvesEachScenario(Scenario scenario, OrderStatus status, int attempts)
    {
        var store = new OrderStore(TimeProvider.System); var session = Guid.NewGuid(); store.Submit(session, Guid.NewGuid(), scenario);
        var work = await store.Reader.ReadAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var processor = new OrderProcessor(store, TimeProvider.System); await processor.ProcessAsync(work, timeout.Token);
        var order = Assert.Single(store.List(session)); Assert.Equal(status, order.Status); Assert.Equal(attempts, order.Attempts);
        Assert.Equal(attempts - 1, order.Events.Count(x => x.Message.StartsWith("Temporary failure")));
        await processor.ProcessAsync(work, timeout.Token); Assert.Equal(order.Events.Count, Assert.Single(store.List(session)).Events.Count);
    }
    [Fact]
    public async Task CancellationStopsWork()
    {
        var store = new OrderStore(TimeProvider.System); var session = Guid.NewGuid(); store.Submit(session, Guid.NewGuid(), Scenario.Recover);
        var work = await store.Reader.ReadAsync(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrderProcessor(store, TimeProvider.System).ProcessAsync(work, cancellation.Token));
        Assert.Equal(OrderStatus.Queued, Assert.Single(store.List(session)).Status);
    }
    [Fact]
    public void CompletedOrdersExpireButQueuedWorkDoesNot()
    {
        var clock = new FakeTimeProvider(); var store = new OrderStore(clock); var session = Guid.NewGuid();
        var order = store.Submit(session, Guid.NewGuid(), Scenario.Success).Order!;
        store.Advance(new(session, order.Id), OrderStatus.Processing, 1, "Working"); store.Advance(new(session, order.Id), OrderStatus.Completed, 1, "Done");
        var queued = store.Submit(session, Guid.NewGuid(), Scenario.Success).Order!; clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(queued.Id, Assert.Single(store.List(session)).Id);
    }
    [Fact]
    public void InvalidTransitionDoesNotCorruptSnapshots()
    {
        var store = new OrderStore(TimeProvider.System); var session = Guid.NewGuid(); var before = store.Submit(session, Guid.NewGuid(), Scenario.Success).Order!;
        Assert.Throws<InvalidOperationException>(() => store.Advance(new(session, before.Id), OrderStatus.Completed, 1, "Invalid"));
        store.Advance(new(session, before.Id), OrderStatus.Processing, 1, "Working");
        Assert.Equal(OrderStatus.Queued, before.Status); Assert.Single(before.Events); Assert.Equal(2, Assert.Single(store.List(session)).Events.Count);
    }
    [Fact]
    public void BackoffIsOneThenTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Delay(1)); Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.Delay(2));
    }
}
public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;
    public ApiTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();
    [Fact]
    public async Task HealthAndDashboardAreServed()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var page = await client.GetAsync("/"); Assert.Contains("OrderFlow", await page.Content.ReadAsStringAsync()); Assert.True(page.Headers.Contains("Content-Security-Policy"));
    }
    [Fact]
    public async Task MissingSessionIsRejected() => Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/orders")).StatusCode);
    [Theory]
    [InlineData("nonsense")]
    [InlineData("1")]
    public async Task InvalidScenarioIsRejected(string scenario)
    {
        using var request = Request(Guid.NewGuid(), Guid.NewGuid(), scenario); Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }
    [Fact]
    public async Task AcceptedReplayAndConflictHaveDistinctHttpSemantics()
    {
        var session = Guid.NewGuid(); var key = Guid.NewGuid(); using var first = Request(session, key, "Success");
        var accepted = await client.SendAsync(first); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); Assert.True(accepted.Headers.CacheControl!.NoStore);
        using var second = Request(session, key, "Success"); var replay = await client.SendAsync(second); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("\"replayed\":true", await replay.Content.ReadAsStringAsync());
        using var conflict = Request(session, key, "Exhaust"); Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(conflict)).StatusCode);
    }
    private static HttpRequestMessage Request(Guid session, Guid key, string scenario)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { scenario }) };
        request.Headers.Add("X-Demo-Session", session.ToString()); request.Headers.Add("Idempotency-Key", key.ToString()); return request;
    }
}
