# Contributing to OrderFlow

Small, focused changes are welcome. OrderFlow is an executable ASP.NET Core architecture demo; its purpose is to make background-job behavior understandable.

## Development

1. Fork and clone the repository.
2. Install the .NET 10 SDK.
3. Run `dotnet test --configuration Release`. For frontend changes, install Node.js 24+ and run `node --test tests/frontend/*.test.mjs` (no dependencies to install).
4. Run `dotnet run --project src/OrderFlow --urls http://localhost:5088`.
5. Check successful delivery, recovery, exhausted retries, and repeating the last request in the browser.

No secrets, database, or private services are needed. Do not contribute proprietary code or actual customer/order data.

## Pull requests

Explain the behavior before and after your change, the relevant tests, and any change to queue, idempotency, cancellation, or retention guarantees. Add a regression test for fixes. For UI changes, include a screenshot and check narrow layouts, keyboard navigation, and readable focus states. Don't claim accessibility compliance from automated checks alone.

Discuss database, broker, authentication, or architecture changes in an issue first. Prefer optional adapters over turning the demo into an infrastructure setup exercise. Avoid cosmetic abstraction layers without a concrete need.

## Good starting points

- Extend API boundary coverage (for example malformed JSON or unsupported content types). [Issue #3](https://github.com/RamySaleeb/dotnet-orderflow/issues/3) covers UUID validation and session-scoped replay; keep fresh sessions and stay below the shared submission limit.
- Extend assistive-technology verification of outcome announcements; record the browser and screen reader used. Check quiet initial history, grouped completions, replay deduplication, and focus retention.
- Review [ADR 0001](docs/decisions/0001-optional-durable-storage.md) and its small implementation milestones. Agree on a milestone with the maintainer before implementing; the optional PostgreSQL adapter is proposed, not available. The admission/query and single-attempt boundaries are implemented in memory and covered by `AdapterTests.cs`; persistence and leases are still proposed. Preserve SDK-only startup and add real crash/duplicate-delivery tests before claiming durability.

For bugs, include reproduction steps, expected/actual results, SDK version, and relevant sanitized logs. Do not include session tokens from a shared instance or credentials.
