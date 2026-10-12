using System.Threading.Channels;

namespace OrderFlow;

public enum Scenario { Success, Recover, Exhaust }
public enum OrderStatus { Queued, Processing, Retrying, Completed, Failed }
public sealed record OrderEvent(DateTimeOffset At, string Message);
public sealed record OrderView(Guid Id, Scenario Scenario, OrderStatus Status, int Attempts, IReadOnlyList<OrderEvent> Events);
public sealed record Submission(OrderView? Order, bool Replayed, string? Error);
public sealed record WorkItem(Guid Session, Guid OrderId);

// Store and queue admission share one lock: an accepted request is always queued exactly once.
// This is an in-process demo, not a durable message broker or a payment system.
public sealed class OrderStore(TimeProvider clock, int capacity = 500, int perSession = 20)
{
    private sealed class Entry(Guid session, Guid key, Scenario scenario, DateTimeOffset now)
    {
        public Guid Session { get; } = session;
        public Guid Key { get; } = key;
        public Guid Id { get; } = Guid.NewGuid();
        public Scenario Scenario { get; } = scenario;
        public OrderStatus Status { get; set; } = OrderStatus.Queued;
        public int Attempts { get; set; }
        public List<OrderEvent> Events { get; } = [new(now, "Accepted and queued")];
        public OrderView View() => new(Id, Scenario, Status, Attempts, Events.ToArray());
    }

    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private readonly Channel<WorkItem> queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });
    public ChannelReader<WorkItem> Reader => queue.Reader;

    public Submission Submit(Guid session, Guid key, Scenario scenario)
    {
        lock (gate)
        {
            Prune();
            var existing = entries.Find(x => x.Session == session && x.Key == key);
            if (existing is not null)
                return existing.Scenario == scenario
                    ? new(existing.View(), true, null)
                    : new(null, false, "This idempotency key belongs to a different scenario.");
            if (entries.Count >= capacity || entries.Count(x => x.Session == session) >= perSession)
                return new(null, false, "Demo capacity reached. Completed orders expire after 30 minutes.");
            var entry = new Entry(session, key, scenario, clock.GetUtcNow());
            if (!queue.Writer.TryWrite(new(session, entry.Id)))
                return new(null, false, "The demo queue is full. Try again later.");
            entries.Add(entry);
            return new(entry.View(), false, null);
        }
    }

    public IReadOnlyList<OrderView> List(Guid session)
    {
        lock (gate)
        {
            Prune();
            return entries.Where(x => x.Session == session).Select(x => x.View()).Reverse().ToArray();
        }
    }

    public OrderView? Advance(WorkItem work, OrderStatus status, int attempt, string message)
    {
        lock (gate)
        {
            var entry = entries.Find(x => x.Session == work.Session && x.Id == work.OrderId);
            if (entry is null || entry.Status is OrderStatus.Completed or OrderStatus.Failed) return null;
            var valid = status switch
            {
                OrderStatus.Processing => entry.Status is OrderStatus.Queued or OrderStatus.Retrying && attempt == entry.Attempts + 1,
                OrderStatus.Retrying => entry.Status == OrderStatus.Processing && attempt == entry.Attempts && attempt < 3,
                OrderStatus.Completed or OrderStatus.Failed => entry.Status == OrderStatus.Processing && attempt == entry.Attempts,
                _ => false
            };
            if (!valid) throw new InvalidOperationException("Invalid order transition.");
            entry.Status = status;
            entry.Attempts = attempt;
            entry.Events.Add(new(clock.GetUtcNow(), message));
            return entry.View();
        }
    }

    private void Prune() => entries.RemoveAll(x => x.Status is OrderStatus.Completed or OrderStatus.Failed
        && clock.GetUtcNow() - x.Events[^1].At > TimeSpan.FromMinutes(30));
}

public static class RetryPolicy
{
    public const int MaxAttempts = 3;
    public static bool ShouldFail(Scenario scenario, int attempt) => scenario == Scenario.Exhaust || scenario == Scenario.Recover && attempt < MaxAttempts;
    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
}

public sealed class OrderProcessor(OrderStore store, TimeProvider clock)
{
    public async Task ProcessAsync(WorkItem work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RetryPolicy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = store.Advance(work, OrderStatus.Processing, attempt, $"Attempt {attempt}: contacting simulated fulfillment");
            if (order is null) return;
            await Task.Delay(TimeSpan.FromMilliseconds(700), clock, cancellationToken);
            if (!RetryPolicy.ShouldFail(order.Scenario, attempt))
            {
                store.Advance(work, OrderStatus.Completed, attempt, "Fulfillment completed (simulation)");
                return;
            }
            if (attempt == RetryPolicy.MaxAttempts)
            {
                store.Advance(work, OrderStatus.Failed, attempt, "Retry budget exhausted; manual investigation would be required");
                return;
            }
            var delay = RetryPolicy.Delay(attempt);
            store.Advance(work, OrderStatus.Retrying, attempt, $"Temporary failure; retry in {delay.TotalSeconds:0}s");
            await Task.Delay(delay, clock, cancellationToken);
        }
    }
}

public sealed class OrderWorker(OrderStore store, OrderProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Consume(stoppingToken), Consume(stoppingToken));
    private async Task Consume(CancellationToken token)
    {
        await foreach (var work in store.Reader.ReadAllAsync(token)) await processor.ProcessAsync(work, token);
    }
}
