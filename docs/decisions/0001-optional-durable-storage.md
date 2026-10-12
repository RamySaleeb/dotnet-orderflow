# ADR 0001: Optional durable order storage and work queue

- **Status:** Proposed — documentation only; implementation requires maintainer review.
- **Date:** 2026-10-12
- **Tracks:** [Issue #4](https://github.com/RamySaleeb/dotnet-orderflow/issues/4)
- **Decision proposed:** Add an opt-in PostgreSQL adapter that persists orders and jobs together. Keep the existing in-memory mode as the default.

Nothing in this ADR is implemented or deployed. There are no new packages, migrations, containers, configuration switches, or cloud resources in this change. The current `dotnet run --project src/OrderFlow --urls http://localhost:5088` command still needs only the .NET SDK.

## Context and scope

Today `OrderStore` owns both a bounded `Channel<WorkItem>` and an in-memory list. One lock makes duplicate detection, capacity checks, queue admission, and insertion atomic within the process. `OrderWorker` runs two consumers. `OrderProcessor` waits 700ms for simulated fulfillment, retries after 1s and 2s, and stops after three attempts. Terminal records expire after 30 minutes; restarting loses all records and pending work.

Persisting only the order list would introduce a gap: an HTTP request could commit an order and crash before putting work into the channel. Replacing only the channel would leave persistent jobs referring to lost order state. The optional adapter must own the acceptance transaction, not expose independent save and enqueue calls to the API.

This proposal keeps fictional orders and the existing dashboard/API contract. Real payments, customer records, authentication, cloud provisioning, and a mandatory broker are outside scope.

## Guarantees and limits

| Concern | Proposed durable-mode contract |
| --- | --- |
| Acceptance | Return 202 only after one database transaction commits the order, first event, idempotency identity, and ready job. Failed/rolled-back acceptance creates none of them. |
| Restart | Committed unfinished work remains discoverable after API/worker restart. This assumes a healthy persistent database and durable commits; it does not cover lost disks, untested backups, or asynchronous failover losing acknowledged writes. |
| Ambiguous response | If the connection fails after commit, the client retries with the same session/key. It receives the original ID with 200; a different scenario receives 409. Never generate a replacement key automatically after a timeout. |
| Idempotency | A unique `(session_id, idempotency_key)` identity protects concurrent requests across instances. Store the normalized scenario, not raw JSON spelling, for payload comparison. The same key in two sessions creates two independent orders. |
| Delivery | Work may be claimed again after a crash. Delivery is at least once while workers/database remain available and recovery budgets are not exhausted. No strict FIFO guarantee. |
| State transitions | Only the current, unexpired lease owner may change processing state. Terminal state is immutable. Every state/event/job update commits atomically. |
| Retention | Retain terminal orders, events, and idempotency identity for at least 30 minutes after terminal completion. Never prune active jobs. A transactional cleanup removes terminal job/events/order together. Actual deletion ends deduplication; after deletion, reusing the key may create new work. |
| Session isolation | Every API read and replay remains scoped to its session. The random session UUID is a demo capability, not authenticated identity. Persistence does not make it authentication. |
| External effects | Database deduplication and lease fencing cannot guarantee exactly-once fulfillment. The default fulfillment remains a simulator. See the external-effect section below. |

Use `fsync=on` and synchronous commit for this contract; weaker settings invalidate the stated crash durability. PostgreSQL documents the tradeoffs in its [WAL configuration reference](https://www.postgresql.org/docs/current/runtime-config-wal.html). A named local volume protects against container replacement, not volume deletion or disk failure.

## Options considered

| Option | Acceptance and delivery boundary | Benefits | Costs and risks |
| --- | --- | --- | --- |
| Current memory + Channel | One process lock; no persistence | Zero configuration, fast learning loop | Restart loses orders, work, and replay identity |
| **PostgreSQL order store + job table (proposed first adapter)** | Insert order/event/job in one transaction; workers claim committed jobs directly | One optional service; no commit-to-enqueue gap; atomic completion/retry updates | Polling and lease management, database maintenance, no broker fan-out; queue traffic shares database capacity |
| PostgreSQL + external broker + transactional outbox | Commit order/event/outbox in one transaction; relay publishes and then marks dispatched | Separates delivery load, supports future routing/fan-out | Database plus broker plus relay; duplicate publication and delivery; more failure modes and operations |

For the broker option, publishing before the database commit risks orphan messages; publishing after commit without an outbox risks lost work. An outbox relay publishes with a stable message ID, waits for the broker's durable confirmation, then marks the row dispatched. A crash between confirmation and marking causes publication again. Consumers must still acquire a database lease and fence state updates; they acknowledge delivery only after the outcome/retry transaction commits. Persist retry scheduling in the database and create the next outbox record in that same transaction. A terminal duplicate can be acknowledged without another effect. Broker retention, confirmation, and recovery behavior would need a separate ADR and integration tests before selecting a product.

**Recommendation:** Start with the database queue. OrderFlow has two consumers and no measured need for broker routing or independent delivery scaling. Revisit the broker option only with a concrete workload and operating requirement. This is a project design choice, not a throughput benchmark.

## Proposed schema and acceptance transaction

Use PostgreSQL 18 as the initial integration-test target; pin and maintain a supported patch/image digest when implementing. Schema names below are illustrative, not executable migrations.

| Table | Essential columns and constraints |
| --- | --- |
| `orders` | `id` UUID primary key; `session_id`, `idempotency_key` non-empty UUIDs; normalized `scenario`; `status`; `attempts` (0–3); `created_at`, nullable `terminal_at`; unique `(session_id, idempotency_key)` |
| `order_events` | `order_id` foreign key; monotonic per-order `sequence`; timestamp and structured event kind; unique `(order_id, sequence)`; render safe human-readable messages at the boundary |
| `order_jobs` | `order_id` primary/foreign key; `state` (ready/leased/done/quarantined); `available_at`; nullable `lease_until`; monotonic `lease_generation`; `lease_owner`; `recovery_count`; `resume_attempt` boolean for interrupted logical attempts; sanitized `last_error_code` |
| `admission_guard` | One fixed row locked during acceptance/cleanup to serialize the demo's global/per-session capacity accounting |
| `schema_versions` | Applied migration version/checksum; changes applied by an explicit migration command |

Index due ready jobs by `(available_at, order_id)`, leased jobs by `lease_until`, session listings by `(session_id, created_at)`, and terminal cleanup by `terminal_at`. Add checks for valid states, legal attempt ranges, and matching lease fields. Foreign keys prevent orphan jobs/events. Do not rely on application pre-checks for unique-key correctness.

In a short acceptance transaction:

1. Lock `admission_guard`. Find an existing session/key first: equal normalized payload returns its existing snapshot; conflicting payload returns 409, even at capacity.
2. For new identities, check retained-order capacity (500 global, 20 per session) using this serialization boundary. Cleanup uses the same lock. This intentionally simple approach trades admission throughput for clear quota correctness.
3. Insert a Queued order, sequence-1 acceptance event, and ready job with database `available_at` now. Commit before returning 202. Roll back all inserts on failure.

Do not publish an in-memory message as the source of truth. Optional wake-up notifications may reduce polling latency, but periodic database scans must recover missed notifications. GET builds an immutable snapshot of each order and its events from one consistent database read. Do not hold locks while writing HTTP responses.

## Worker lifecycle, leases, and retries

Proposed starting values: two consumers, 30-second leases, renewal every 10 seconds, and a 10-second attempt timeout. These are tunable design defaults requiring failure tests, not measured service limits. Use the database clock for due times and lease comparisons so hosts with different clocks agree.

1. **Claim:** In a short transaction, select a due ready job using `FOR UPDATE SKIP LOCKED`, lock its order, increment the lease generation, assign owner/expiry, increment the business attempt, and append Processing. Commit, then execute outside the transaction. PostgreSQL explicitly describes `SKIP LOCKED` as useful for queue-like tables, while warning that it is unsuitable as a general consistent read: [SELECT reference](https://www.postgresql.org/docs/current/sql-select.html).
2. **Renew/fence:** Renewal and outcome writes require matching order ID, owner, generation, and an unexpired lease. Check affected-row counts. A failed renewal cancels local work and prevents any subsequent state write by that owner. A stopped or partitioned worker can still execute outside the database; fencing protects stored state, not a remote side effect.
3. **Complete:** Lock/check the job lease and atomically append Completed, set terminal time, and mark the job done. A duplicate claim/delivery of a terminal job is a no-op. Commit failure leaves recovery to the lease protocol.
4. **Retry:** A known transient failure atomically appends Retrying, clears the lease, and sets the job ready with `available_at = database_now + delay`. Preserve the current 1s/2s delays and three-attempt budget for this demo. A consumer is free to claim other jobs during the wait; no sleeping while holding a lease or transaction. Attempt three failing sets Failed and done atomically.
5. **Recover:** A sweeper claims expired leases under row locks. For the simulator, it requeues the interrupted logical attempt with a recovery marker; reclaim uses the existing attempt number rather than incrementing it. A new generation fences the old owner. Completed business failures consume the three-attempt budget; process crashes consume a separate recovery budget (proposed maximum five per job). Record recovery events separately from new Processing attempts so the timeline tells the truth. The existing processor loop must be adapted; simply invoking it again from attempt one is incorrect.
6. **Poison work:** Invalid persisted payload/schema, a permanent validation error, or exhausted recovery budget quarantines the job and atomically marks the order Failed with a sanitized reason. This is a database quarantine, not a claimed broker dead-letter queue. No automatic infinite redelivery. Future manual redrive must be explicit, audited, and maintain the original identity/effect key; it is not part of this first adapter.
7. **Shutdown/cancellation:** Stop claiming first; give in-flight work a bounded drain interval (proposed 10 seconds). If cancellation occurs before an effect starts, release/requeue under the lease guard. If completion cannot be confirmed, leave the lease to expire rather than claiming success or failure. A hard kill follows the same recovery path. Host cancellation is not a business retry failure. There is no user-facing cancel-order endpoint in scope.

Acceptance/cleanup acquire the admission guard first; cleanup then locks jobs before orders, matching workers. Claim, renewal, completion, and recovery use a consistent job-then-order locking order; document it in code. Retry deadlock/serialization failures at the transaction boundary with bounded retries, using the same request identity. Never hold a transaction across simulated or external fulfillment.

## Duplicate delivery versus external effects

For the simulator, re-executing an interrupted attempt has no real external effect. A future HTTP fulfillment adapter introduces an ambiguous window: the provider may succeed while the worker crashes before recording Completed.

Such an adapter must use a stable provider idempotency key derived from the order ID and operation (never the lease generation or attempt number), retain it for the full recovery horizon, and reconcile provider results after ambiguous timeouts. Confirm the provider's actual key scope, lifetime, and payload-conflict behavior. Database transactions cannot atomically commit with an arbitrary external API.

Without provider deduplication/reconciliation, expired work with an uncertain effect must enter quarantine for investigation instead of being blindly retried. If required, add an explicit Unknown/NeedsReview domain state in a separately reviewed API/UI change. Neither an inbox table nor a broker acknowledgement alone solves this window. Do not advertise exactly-once external effects.

## Narrow adapter boundaries

Names below are proposed contracts, not existing interfaces:

- `IOrderAdmission.SubmitAsync(session, key, scenario, cancellation)` owns atomic identity lookup, quota enforcement, order/event/job creation, and returns Accepted/Replay/Conflict. No separate API-level `Save` then `Enqueue` pair.
- `IOrderQueries.ListAsync(session, cancellation)` returns the current immutable views with the same session scope.
- `IOrderWork.ClaimAsync`, `RenewAsync`, and `ResolveAsync` return/use a lease token. Resolve accepts Completed, RetryAt, or Failed and commits order/event/job changes together. Recovery and cleanup remain adapter-owned maintenance operations.
- `IFulfillmentSimulator.ExecuteAttemptAsync` performs one cancellable attempt. The scheduler owns attempts and deadlines; it does not restart the current three-attempt loop on each delivery.

Keep `Scenario`, `OrderView`, HTTP validation, replay status codes, and dashboard rendering independent of persistence. Extract boundaries only when building the adapter and proving parity. Memory mode may wrap the current lock/Channel implementation; do not force database lease machinery into the beginner path solely for interface symmetry.

## Opt-in setup, migrations, operations, and privacy

**Proposed configuration, unavailable today:** `OrderFlow__Storage=Memory` when absent; `Postgres` explicitly selected with `ConnectionStrings__OrderFlow` provided through local user-secrets or environment injection. Unknown modes fail fast. Postgres mode with an unavailable database or incompatible schema fails readiness and rejects acceptance; never silently fall back to memory and lose durability.

A later local-only Compose profile would start PostgreSQL with a named volume and a loopback-bound port. A contributor would explicitly start it, configure a development credential outside version control, run the migration command, then run the same app with Postgres selected. The default project, default tests, and existing app container remain usable without Docker or PostgreSQL. Separate optional database tests/CI provision a disposable database; no cloud subscription is needed. Switching modes does not import in-memory orders; show the selected mode and its retention behavior in the dashboard when implemented.

Version migrations in source control and serialize their application with a migration lock. Do not auto-migrate on every API replica startup. Test clean installation and upgrades from the previous schema; use additive changes first, back up before destructive changes, and prefer forward fixes over automatic destructive downgrades. Readiness should check connectivity and supported schema separately from process liveness.

Costs include database memory/disk, connections, WAL growth, polling, indexes, vacuum/cleanup, backups, and operator attention. Monitor pending/due job counts, oldest ready age, lease recovery rate, retry/quarantine counts, transaction failures, storage size, and acceptance latency. Start with bounded batches and capped polling backoff; measure before increasing consumers. The current per-process 60/minute limiter remains unsuitable as a distributed global limit; multiple replicas require a separately specified shared policy. Durable quotas alone do not solve request-rate limiting.

Persist only fictional fixtures and necessary state. Treat session UUIDs as capability secrets: redact them from logs and metrics, omit connection strings and raw exception payloads, use parameterized SQL and least-privilege database roles, and use TLS for non-local database connections. No session ID should be a metric label. Backups and database logs may retain data beyond the 30-minute application TTL; document their own retention and deletion policy. Losing browser sessionStorage does not delete persistent rows, and a shared database deployment still requires an authentication design before real use.

## Required failure-test matrix for implementation

These are future acceptance tests; the existing 27 backend and 12 frontend tests do not prove durability. Use real PostgreSQL transactions and independently killable worker/API processes, deterministic barriers around commit/effect boundaries, and controlled due/lease timestamps. Avoid tests that merely sleep and hope a race occurred.

| Failure or boundary | Expected evidence |
| --- | --- |
| Kill before acceptance commit | No order, event, or job survives; corrected retry creates one order. |
| Kill after commit / before enqueue or HTTP response | Database job already exists; restart processes it; same-key HTTP retry returns original ID. No channel enqueue is required. |
| Simultaneous same-session/key POSTs | One order/job; one 202, other equal-payload requests 200; conflicting payload 409. |
| Same key across sessions | Distinct IDs and isolated GET/replay, matching current HTTP tests. |
| Duplicate job delivery / terminal replay | Only one accepted state transition; terminal events unchanged. |
| Worker killed during processing | Expired job recovers its logical attempt with a new lease generation; no lost committed work. |
| Lease expiry with old worker still running | New owner can proceed; old completion/renewal updates zero rows and cannot overwrite state. |
| Restart during scheduled retry | Job stays unavailable before stored due time and becomes claimable at the deadline; attempt budget survives restart. |
| Retry exhaustion / permanent poison payload | Exactly three business attempts for Exhaust; Failed/quarantined as applicable, no further automatic claims. |
| Repeated recovery crashes | Recovery count persists; quarantine at the configured limit, no unbounded loop. |
| Shutdown / cancellation | No new claims after stopping; safe unstarted work requeues; uncertain in-flight work waits for recovery. |
| Effect succeeds, then completion commit fails | Simulator safely recovers; future provider adapter deduplicates/reconciles with the same effect key or quarantines uncertainty. |
| Database outage / ambiguous commit | No false 202; a same-key retry after reconnect resolves whether acceptance committed. |
| Retention races with submit/replay | Active jobs retained; terminal cleanup atomic; replay survives until deletion, and key reuse after deletion is documented. |
| Migration and mode selection | Fresh/upgrade schema tests pass; incompatible schema fails readiness; default startup works with no database. |
| Broker option, if later adopted | Kill relay after confirmation/before marking: duplicate publish is safe; kill consumer after outcome commit/before ack: redelivery is a no-op. |

## Follow-up milestones for maintainer review

1. **Review this ADR:** Agree on PostgreSQL, retention, crash-vs-business-attempt accounting, five-recovery quarantine budget, lease timings, and future external-effect policy. Keep status Proposed until accepted.
2. **Extract and prove memory parity:** Introduce admission/query and single-attempt execution boundaries in a focused PR. Preserve all 39 existing tests, dashboard behavior, and SDK-only startup. No database required yet.
3. **Add optional persistence:** Schema/migration runner, configuration validation, transactional admission and reads, local Compose instructions, and separate PostgreSQL integration tests. Cover commit ambiguity, quotas, concurrent keys, and retention before adding workers.
4. **Add durable execution:** Claim/renew/fence, persisted retry scheduling, crash recovery, quarantine, and graceful shutdown. Pass the failure matrix with process-kill tests before claiming restart durability.
5. **Document and observe:** Mode-aware dashboard copy, operational metrics, backup/restore exercise, and contributor guide. Re-review the adapter's guarantees. Consider an external broker only in a later ADR supported by measured need.

Completion of issue #4 means this proposal is published for review. It does not approve these milestones for implementation or provision infrastructure.
