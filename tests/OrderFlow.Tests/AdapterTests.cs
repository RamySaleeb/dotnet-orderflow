using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OrderFlow;
using Xunit;

namespace OrderFlow.Tests;

public class AdapterTests
{
    [Fact]
    public async Task AdmissionBoundaryKeepsConcurrentReplayAtomicAndQueriesIsolated()
    {
        var store = new OrderStore(new FakeTimeProvider(), capacity: 2, perSession: 1);
        IOrderAdmission admission = store;
        IOrderQueries queries = store;
        var session = Guid.NewGuid();
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(
            async () => await admission.SubmitAsync(session, key, Scenario.Recover, default))));
        var created = Assert.Single(results, x => !x.Replayed);
        Assert.All(results, x => { Assert.Null(x.Error); Assert.Equal(created.Order!.Id, x.Order!.Id); });
        Assert.True(store.Reader.TryRead(out var work));
        Assert.Equal(created.Order!.Id, work.OrderId);
        Assert.False(store.Reader.TryRead(out _));
        Assert.Empty(await queries.ListAsync(Guid.NewGuid(), default));
        var snapshot = Assert.Single(await queries.ListAsync(session, default));
        Assert.NotNull((await admission.SubmitAsync(session, key, Scenario.Exhaust, default)).Error);
        Assert.NotNull((await admission.SubmitAsync(session, Guid.NewGuid(), Scenario.Success, default)).Error);
        Assert.True((await admission.SubmitAsync(session, key, Scenario.Recover, default)).Replayed);
        store.Advance(work, OrderStatus.Processing, 1, "Working");
        Assert.Equal(OrderStatus.Queued, snapshot.Status);
        Assert.Single(snapshot.Events);
        Assert.Equal(2, Assert.Single(await queries.ListAsync(session, default)).Events.Count);
    }

    [Fact]
    public async Task PreCanceledBoundaryCallsDoNotAdmitOrReadWork()
    {
        var store = new OrderStore(new FakeTimeProvider());
        IOrderAdmission admission = store;
        IOrderQueries queries = store;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await admission.SubmitAsync(session, Guid.NewGuid(), Scenario.Success, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queries.ListAsync(session, cancellation.Token));
        Assert.Empty(await queries.ListAsync(session, default));
        Assert.False(store.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(Scenario.Success, 1, FulfillmentOutcome.Completed)]
    [InlineData(Scenario.Recover, 1, FulfillmentOutcome.TransientFailure)]
    [InlineData(Scenario.Recover, 2, FulfillmentOutcome.TransientFailure)]
    [InlineData(Scenario.Recover, 3, FulfillmentOutcome.Completed)]
    [InlineData(Scenario.Exhaust, 3, FulfillmentOutcome.TransientFailure)]
    public async Task SimulatorExecutesOnlyTheRequestedAttempt(Scenario scenario, int attempt, FulfillmentOutcome expected)
    {
        var clock = new FakeTimeProvider();
        IFulfillmentSimulator simulator = new FulfillmentSimulator(clock);
        var order = new OrderView(Guid.NewGuid(), scenario, OrderStatus.Processing, attempt, []);
        var execution = simulator.ExecuteAttemptAsync(order, default).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(699));
        Assert.False(execution.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(expected, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(attempt, order.Attempts);
        Assert.Empty(order.Events);
    }

    [Fact]
    public async Task SimulatorPropagatesCancellationDuringAnAttempt()
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var order = new OrderView(Guid.NewGuid(), Scenario.Recover, OrderStatus.Processing, 1, []);
        var execution = new FulfillmentSimulator(clock).ExecuteAttemptAsync(order, cancellation.Token).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(350));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(execution.IsCanceled);
        Assert.Empty(order.Events);
    }

    [Fact]
    public async Task ProcessorUsesInjectedOutcomeAndDoesNotExecuteTerminalWorkTwice()
    {
        var clock = new FakeTimeProvider();
        var store = new OrderStore(clock);
        var session = Guid.NewGuid();
        var accepted = store.Submit(session, Guid.NewGuid(), Scenario.Exhaust).Order!;
        var work = await store.Reader.ReadAsync();
        var simulator = new SuccessfulAttempt();
        var processor = new OrderProcessor(store, clock, simulator);
        await processor.ProcessAsync(work, default);
        var completed = Assert.Single(store.List(session));
        Assert.Equal(accepted.Id, completed.Id);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempts);
        Assert.Equal(3, completed.Events.Count);
        await processor.ProcessAsync(work, default);
        Assert.Equal(1, simulator.Calls);
        Assert.Equal(completed.Events, Assert.Single(store.List(session)).Events);
    }

    private sealed class SuccessfulAttempt : IFulfillmentSimulator
    {
        public int Calls { get; private set; }
        public ValueTask<FulfillmentOutcome> ExecuteAttemptAsync(OrderView order, CancellationToken cancellationToken)
        {
            Assert.Equal(OrderStatus.Processing, order.Status);
            Assert.Equal(1, order.Attempts);
            Calls++;
            return ValueTask.FromResult(FulfillmentOutcome.Completed);
        }
    }
}

public class AdapterCompositionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;
    public AdapterCompositionTests(WebApplicationFactory<Program> factory) => this.factory = factory;

    [Fact]
    public void DefaultApiAndWorkerShareOneMemoryStore()
    {
        var services = factory.Services;
        var store = services.GetRequiredService<OrderStore>();
        Assert.Same(store, services.GetRequiredService<IOrderAdmission>());
        Assert.Same(store, services.GetRequiredService<IOrderQueries>());
        Assert.IsType<FulfillmentSimulator>(services.GetRequiredService<IFulfillmentSimulator>());
        Assert.NotNull(services.GetRequiredService<OrderProcessor>());
    }

    [Fact]
    public async Task HttpEndpointsUseReplaceableAsyncBoundaries()
    {
        var adapter = new StubAdapter();
        using var replaced = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOrderAdmission>();
            services.RemoveAll<IOrderQueries>();
            services.AddSingleton<IOrderAdmission>(adapter);
            services.AddSingleton<IOrderQueries>(adapter);
        }));
        using var client = replaced.CreateClient();
        var session = Guid.NewGuid();
        var key = Guid.NewGuid();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        { Content = JsonContent.Create(new { scenario = "Recover" }) };
        post.Headers.Add("X-Demo-Session", session.ToString());
        post.Headers.Add("Idempotency-Key", key.ToString());
        using var accepted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal((session, key, Scenario.Recover), adapter.Received);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        get.Headers.Add("X-Demo-Session", session.ToString());
        using var listed = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var views = await listed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>();
        Assert.Equal(adapter.Order.Id, Assert.Single(views!).GetProperty("id").GetGuid());
        Assert.Equal(session, adapter.QueriedSession);
        Assert.Empty(replaced.Services.GetRequiredService<OrderStore>().List(session));
    }

    private sealed class StubAdapter : IOrderAdmission, IOrderQueries
    {
        public OrderView Order { get; } = new(Guid.NewGuid(), Scenario.Recover, OrderStatus.Queued, 0, []);
        public (Guid, Guid, Scenario) Received { get; private set; }
        public Guid QueriedSession { get; private set; }
        public async ValueTask<Submission> SubmitAsync(Guid session, Guid key, Scenario scenario, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Received = (session, key, scenario);
            return new(Order, false, null);
        }
        public async ValueTask<IReadOnlyList<OrderView>> ListAsync(Guid session, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            QueriedSession = session;
            return [Order];
        }
    }
}
