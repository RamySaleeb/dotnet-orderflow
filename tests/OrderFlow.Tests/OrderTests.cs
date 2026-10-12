using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
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
        var clock = new SignalingFakeTimeProvider();
        var start = clock.GetUtcNow();
        var store = new OrderStore(clock);
        var session = Guid.NewGuid();
        store.Submit(session, Guid.NewGuid(), scenario);
        var work = await store.Reader.ReadAsync();
        using var cancellation = new CancellationTokenSource();
        var processor = new OrderProcessor(store, clock);
        var processing = processor.ProcessAsync(work, cancellation.Token);
        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                // Await timer registration, not thread scheduling or a wall-clock sleep.
                await clock.ExpectDelayAsync(TimeSpan.FromMilliseconds(700));
                AssertState(OrderStatus.Processing, attempt, attempt * 2);
                clock.Advance(TimeSpan.FromMilliseconds(699));
                AssertState(OrderStatus.Processing, attempt, attempt * 2);
                Assert.False(processing.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(1));

                if (attempt == attempts) break;
                var backoff = TimeSpan.FromSeconds(attempt == 1 ? 1 : 2);
                await clock.ExpectDelayAsync(backoff);
                AssertState(OrderStatus.Retrying, attempt, attempt * 2 + 1);
                clock.Advance(backoff - TimeSpan.FromMilliseconds(1));
                AssertState(OrderStatus.Retrying, attempt, attempt * 2 + 1);
                Assert.False(processing.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            // Real time is only a guard against a broken test hanging indefinitely.
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            var order = Assert.Single(store.List(session));
            Assert.Equal(status, order.Status);
            Assert.Equal(attempts, order.Attempts);
            var finalMessage = status == OrderStatus.Completed
                ? "Fulfillment completed (simulation)"
                : "Retry budget exhausted; manual investigation would be required";
            string[] expectedMessages = attempts == 1
                ? ["Accepted and queued", "Attempt 1: contacting simulated fulfillment", finalMessage]
                : ["Accepted and queued", "Attempt 1: contacting simulated fulfillment",
                   "Temporary failure; retry in 1s", "Attempt 2: contacting simulated fulfillment",
                   "Temporary failure; retry in 2s", "Attempt 3: contacting simulated fulfillment", finalMessage];
            int[] expectedMilliseconds = attempts == 1 ? [0, 0, 700] : [0, 0, 700, 1700, 2400, 4400, 5100];
            Assert.Equal(expectedMessages, order.Events.Select(x => x.Message));
            Assert.Equal(expectedMilliseconds.Select(x => start.AddMilliseconds(x)), order.Events.Select(x => x.At));

            // A terminal order cannot be processed again, even if delivered twice.
            await processor.ProcessAsync(work, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(order.Events, Assert.Single(store.List(session)).Events);
            Assert.False(clock.HasUnobservedDelay);
        }
        finally { cancellation.Cancel(); }

        void AssertState(OrderStatus expected, int attempt, int eventCount)
        {
            var order = Assert.Single(store.List(session));
            Assert.Equal(expected, order.Status);
            Assert.Equal(attempt, order.Attempts);
            Assert.Equal(eventCount, order.Events.Count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationDuringBackoffPreventsFurtherAttempts(int cancelAfterAttempt)
    {
        var clock = new SignalingFakeTimeProvider();
        var store = new OrderStore(clock);
        var session = Guid.NewGuid();
        store.Submit(session, Guid.NewGuid(), Scenario.Recover);
        var work = await store.Reader.ReadAsync();
        using var cancellation = new CancellationTokenSource();
        var processing = new OrderProcessor(store, clock).ProcessAsync(work, cancellation.Token);
        try
        {
            for (var attempt = 1; attempt <= cancelAfterAttempt; attempt++)
            {
                await clock.ExpectDelayAsync(TimeSpan.FromMilliseconds(700));
                clock.Advance(TimeSpan.FromMilliseconds(700));
                var backoff = TimeSpan.FromSeconds(attempt == 1 ? 1 : 2);
                await clock.ExpectDelayAsync(backoff);
                if (attempt < cancelAfterAttempt) clock.Advance(backoff);
                else clock.Advance(backoff / 2);
            }

            var before = Assert.Single(store.List(session));
            Assert.Equal(OrderStatus.Retrying, before.Status);
            Assert.Equal(cancelAfterAttempt, before.Attempts);
            Assert.False(processing.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(processing.IsCanceled);

            // Advance past every retry deadline: cancellation must leave no late events.
            clock.Advance(TimeSpan.FromMinutes(10));
            var after = Assert.Single(store.List(session));
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.Attempts, after.Attempts);
            Assert.Equal(before.Events, after.Events);
            Assert.False(clock.HasUnobservedDelay);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task CancellationStopsWork()
    {
        var clock = new SignalingFakeTimeProvider();
        var store = new OrderStore(clock);
        var session = Guid.NewGuid();
        store.Submit(session, Guid.NewGuid(), Scenario.Recover);
        var work = await store.Reader.ReadAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OrderProcessor(store, clock).ProcessAsync(work, cancellation.Token));
        Assert.Equal(OrderStatus.Queued, Assert.Single(store.List(session)).Status);
        Assert.False(clock.HasUnobservedDelay);
    }

    // A test-only handshake: each notification means the fake timer is installed.
    // This avoids advancing past a timer before the processor has scheduled it.
    private sealed class SignalingFakeTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> delays = Channel.CreateUnbounded<TimeSpan>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false });
        public bool HasUnobservedDelay => delays.Reader.TryPeek(out _);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task ExpectDelayAsync(TimeSpan expected)
        {
            var actual = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, actual);
        }
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
// Fresh session IDs isolate cases. This fixture sends 21 POSTs, below the shared
// 60/minute limit; assertions use stable IDs/counts, never worker timing or test order.
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
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidGetSessionIsRejected(string? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        if (session is not null) request.Headers.Add("X-Demo-Session", session);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("X-Demo-Session", null)]
    [InlineData("X-Demo-Session", "not-a-uuid")]
    [InlineData("X-Demo-Session", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Idempotency-Key", null)]
    [InlineData("Idempotency-Key", "not-a-uuid")]
    [InlineData("Idempotency-Key", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidPostHeaderDoesNotCreateAnOrder(string header, string? value)
    {
        var session = Guid.NewGuid();
        var key = Guid.NewGuid();
        using var invalid = Request(session, key, "Success");
        invalid.Headers.Remove(header);
        if (value is not null) invalid.Headers.Add(header, value);
        using var rejected = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(await ListOrders(session));

        // A corrected request is a new admission, not a replay of rejected work.
        var id = await SubmitAndReadId(session, key, HttpStatusCode.Accepted, replayed: false);
        Assert.Equal(id, Assert.Single(await ListOrders(session)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task IdempotencyAndOrderListsAreScopedToEachSession()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var key = Guid.NewGuid();
        var aId = await SubmitAndReadId(a, key, HttpStatusCode.Accepted, replayed: false);
        Assert.Empty(await ListOrders(b));
        var bId = await SubmitAndReadId(b, key, HttpStatusCode.Accepted, replayed: false);
        Assert.NotEqual(aId, bId);
        Assert.Equal(aId, Assert.Single(await ListOrders(a)).GetProperty("id").GetGuid());
        Assert.Equal(bId, Assert.Single(await ListOrders(b)).GetProperty("id").GetGuid());

        Assert.Equal(aId, await SubmitAndReadId(a, key, HttpStatusCode.OK, replayed: true));
        Assert.Equal(bId, await SubmitAndReadId(b, key, HttpStatusCode.OK, replayed: true));
        Assert.Equal(aId, Assert.Single(await ListOrders(a)).GetProperty("id").GetGuid());
        Assert.Equal(bId, Assert.Single(await ListOrders(b)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> SubmitAndReadId(Guid session, Guid key, HttpStatusCode status, bool replayed)
    {
        using var request = Request(session, key, "Success");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(replayed, body.GetProperty("replayed").GetBoolean());
        var id = body.GetProperty("order").GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        return id;
    }

    private async Task<JsonElement[]> ListOrders(Guid session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        request.Headers.Add("X-Demo-Session", session.ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }
    [Theory]
    [InlineData("nonsense")]
    [InlineData("1")]
    public async Task InvalidScenarioIsRejected(string scenario)
    {
        var session = Guid.NewGuid();
        using var request = Request(session, Guid.NewGuid(), scenario);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListOrders(session));
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
