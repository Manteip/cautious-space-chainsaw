## Flash Core – AI Coding Agent Instructions

### Background:
Flash Core is the .NET backend for the Elosity web app (Flash == Elosity), implementing core domain logic, data access, and integrations with Azure services. It follows a modular monorepo architecture with a central solution file and many focused class libraries. Cross-cutting concerns like Mediator pipelines, caching, validation, and telemetry are implemented as reusable behaviors.

### 1. Solution Architecture (Monorepo Style)
- Single .NET solution `Endpoint.Flash.Core.sln` with many focused class libraries. Naming pattern: `Endpoint.Flash.Core.<Area>`; external system integrations live in `Extensions` sub‑names (e.g. `Extensions.CosmosDb`, `Extensions.Redis`, `Extensions.AzureSearch`, `Extensions.Databricks`, `Extensions.Kusto`, `Extensions.EventHub`, `Extensions.ServiceBus`, `Extensions.Function`).
- Cross-cutting infra concerns isolated per extension project; each often has a sibling test project `<Project>.Tests` (note some use `.Test`). Maintain symmetry when adding new integrations.
- Domain layers: `Domain` (core entities + context), `Design.Domain` (design-time models / study design), `Runtime.Domain` (execution-time models), `Services` (higher level orchestrations), `Authorization`, `AutoNumber`, `RuleEngine`, `OpenAI` each encapsulate a bounded concern.

### 2. Configuration & Settings Pattern
- Central config class: `Endpoint.Flash.Core.Domain/Settings.cs` implementing `ISettings`. Values pulled from environment variables named `Endpoint_<Key>` via `GetValue`.
- Composite labels (EnvironmentLabel, ApplicationLabel, etc.) prefix the raw env value with `ReleaseVersion-` at access time—never store the prefixed value.
- When introducing a new configurable value: add property to `Settings`, consume through `ISettings` injection, and ensure environment variable follows the `Endpoint_<PropertyName>` convention.

### 3. Mediator & Pipelines
- Requests derive from `BaseQuery` (in `Extensions.Mediator/Base/BaseQuery.cs`) and usually implement `IRequest<T>` plus optional interfaces: `ICacheableQuery`, `IAuditLogRequest<T>`, validation via FluentValidation validators, etc.
- `BaseQuery` captures `ApplicationContext`, `ISettings`, optional HTTP request (serialized) and holds a mutable `CustomDimensions` dictionary for telemetry.
- Pipeline behaviors (see `Extensions.Mediator/Behaviors/*`): `LoggingBehavior`, `ValidationBehavior`, `CachingBehavior`, `RetryBehavior`, `PerformanceBehavior`, `AuthBehavior`, `RuleEngineValidationBehavior`, `PatchValidationBehavior`, `UnhandledExceptionBehavior`, `CustomDimensionBehavior`, `PreviewSessionBehavior`, `QueryAuditTrackingBehavior` (audit + EventGrid).
- To skip behaviors for special internal calls set `SkipBehavior = true` on the query (individual behaviors check this flag where relevant).

### 4. Caching Convention
- Implement `ICacheableQuery` (see `Extensions.Redis/Interface/IBaseCache.cs`) to opt into `CachingBehavior`.
- Required members: `BypassCache`, `CacheKey`, `TtlInSecond`, `ModelDocTypes`, `GetCacheKey()` (helper for key composition). Behavior logic: if `Settings.AllowBypassCache && query.BypassCache` then pipeline bypasses cache; else fetch or populate with sliding expiration (`TtlInSecond` seconds or `Settings.MaxCacheTimeInMinutes`).
- When designing a cache key include tenant/study identifiers if present (`IStudyBaseCache` adds SponsorId, StudyId, StudyVersionId; `IStudyRuntimeBaseCache` adds EnvironmentId) to avoid collisions.

### 5. Validation & Exceptions
- FluentValidation validators auto‑applied by `ValidationBehavior`. On failure throws `FlashValidationException` (see `Common/FlashException/FlashValidationException.cs`) carrying a structured `ValidationErrorModel` used by consumers; always populate clear `PropertyName` values in validators.

### 6. Telemetry & Auditing
- Enrich telemetry by adding key/value pairs to `CustomDimensions` before returning from a handler. Behaviors (logging, audit, performance) read these values; keep keys concise and stable.
- Audit events: `QueryAuditTrackingBehavior` publishes to EventGrid (see usage inside `CachingBehavior` after cache hits to still produce tracking events).

### 7. Adding a New Request Example
```csharp
public class GetFooQuery : CacheableQuery, IRequest<FooDto>, IAuditLogRequest<FooDto> {
    public GetFooQuery(ApplicationContext ctx, ISettings settings) : base(ctx, settings) {}
    public override Dictionary<string,string> CustomDimensions { get; set; } = new() {{"Operation","GetFoo"}};
}
```
Add a validator (FluentValidation) and tests under `Endpoint.Flash.Core.<Area>.Tests` mirroring the project owning the handler.

### 8. Build & Test Workflow (Local)
```powershell
dotnet restore .\Endpoint.Flash.Core.sln
dotnet build .\Endpoint.Flash.Core.sln -c Debug
dotnet test .\Endpoint.Flash.Core.sln -c Debug --no-build
```
CI uses reusable workflow `org-devop-reusable-workflows/code-quality.yml` (see `.github/workflows/core_ci.yml`) running SonarCloud analysis; keep solution path stable or update the workflow input.

### 9. Patterns to Preserve
- Keep new cross-cutting behaviors isolated under `Extensions.Mediator/Behaviors`.
- For any new external service integration: create `Endpoint.Flash.Core.Extensions.<Service>` and optionally a `.Tests` sibling; surface only minimal abstractions publicly.
- Respect environment variable indirection (never hard-code secrets, rely on `Settings`).

### 10. PR Review – Azure Cost & Efficiency (Human or AI)
When reviewing diffs, scan for Azure usage patterns that inflate cost or latency. Keep in mind that multiple other repositories/projects also interact with these services, so poor choices may be hidden from consumers. Flag any anti-patterns or inefficiencies:
- Cosmos DB: prefer bulk or transactional batch over N single `ReadItemAsync` in loops; avoid read-before-upsert if ETag/patch semantics suffice; collapse existence checks + read into a single operation when possible.
- Redis / Caching: ensure hot-path queries implement `ICacheableQuery`; avoid per-item cache writes inside tight loops (buffer then multi-set if added in future); validate `BypassCache` not left `true` in committed code except tests/tools.
- Azure Search: batch indexing vs single document pushes; reuse `SearchClient` / `IndexClient` (no recreating per request); filter projections to only required fields.
- Service Bus / Event Hub: avoid synchronous send loops—use `SendMessagesAsync` with a batch; do not create a new client per message (pool via DI); confirm session usage aligns with `SessionIdsPerServiceBusClient` from `Settings`.
- Blob / Storage: stream directly (no full in-memory buffering for large payloads); skip redundant existence checks—`Upload` with overwrite flag where safe.
- HTTP / OpenAI / External APIs: consolidate sequential calls when they can run in parallel up to `Settings.MaxDegreeOfParallelism`; chunk size respects `Settings.MaxChunkSize`.
- Logging / Telemetry: avoid `LogInformation` inside high-frequency loops—promote to aggregated metrics or add a sampling guard; keep `CustomDimensions` small and stable keys.
- JSON (de)serialization: avoid double serialization (passing already serialized payload back into `JsonConvert.SerializeObject`).
- Retry: confirm use of central `RetryBehavior` instead of ad-hoc try/catch + manual sleeps.
- Validation / Data shaping: filter large collections early (server-side query filters) before projecting to DTOs.
Flag any pattern adding >O(1) remote calls per logical request. Suggest batching, caching, or pipeline reuse instead of micro-optimizing local CPU.