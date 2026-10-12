namespace OrderFlow;

// Admission owns deduplication, capacity checks, and enqueueing as one operation.
// Cancellation is observed before admission; once admitted, work is not rolled back
// because an HTTP caller disconnects. A retry must keep the same session/key.
public interface IOrderAdmission
{
    ValueTask<Submission> SubmitAsync(Guid session, Guid key, Scenario scenario, CancellationToken cancellationToken);
}

public interface IOrderQueries
{
    ValueTask<IReadOnlyList<OrderView>> ListAsync(Guid session, CancellationToken cancellationToken);
}

public enum FulfillmentOutcome { Completed, TransientFailure }

// Executes exactly one attempt; scheduling, events, and retry budgets belong to the processor.
public interface IFulfillmentSimulator
{
    ValueTask<FulfillmentOutcome> ExecuteAttemptAsync(OrderView order, CancellationToken cancellationToken);
}

public sealed class FulfillmentSimulator(TimeProvider clock) : IFulfillmentSimulator
{
    public async ValueTask<FulfillmentOutcome> ExecuteAttemptAsync(OrderView order, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700), clock, cancellationToken);
        return RetryPolicy.ShouldFail(order.Scenario, order.Attempts)
            ? FulfillmentOutcome.TransientFailure
            : FulfillmentOutcome.Completed;
    }
}
