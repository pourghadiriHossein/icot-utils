# Agent Knowledge Base: icot-utils

This file serves as the definitive reference for any AI agent interacting with the `icot-utils` directory. `icot-utils` contains the core shared libraries used across all 13+ microservices in the ICOT platform.

## 📦 Architecture & Packages
The directory contains the primary NuGet package:

### 1. ICOT.Core
The backbone of the microservice architecture. It provides standardized behaviors, middlewares, and extensions so microservices don't have to rewrite boilerplate.
- **Base Entities**: Standardized base classes for EF Core (e.g., `BaseEntity`).
- **Standardized Responses**: The `ApiResponse<T>` wrapper ensures all microservices return exactly the same JSON structure.
- **Global Error Handling**: `GlobalExceptionHandler` automatically intercepts exceptions and formats them into an `ApiResponse`.
- **OpenAPI & Scalar Docs**: `PlatformApiExtensions.cs` configures OpenAPI. It includes an `OperationTransformer` that globally injects `x-account`, `x-language`, and `x-workspace` as default header parameters into all endpoints across all microservices. **Crucially, because .NET 9's Microsoft.AspNetCore.OpenApi does not natively map XML comments or Minimal API summary attributes to MVC Controllers**, this transformer explicitly extracts `[EndpointSummary("...")]` and `[EndpointDescription("...")]` (from `Microsoft.AspNetCore.Http`) and applies them to the OpenAPI schema. When documenting endpoints, agents **MUST** use these attributes instead of C# XML comments (`///`). Internal service-to-service endpoints MUST NOT appear in Scalar documentation and must be decorated with `[ApiExplorerSettings(IgnoreApi = true)]`.
- **Proxy Configuration**: Configures `ForwardedHeadersOptions` to trust the internal API Gateway (YARP), ensuring Swagger generates correct HTTPS/Host URLs.
- **Messaging**: Handles inter-service communication using MassTransit and RabbitMQ.
- **EF Core Interceptors, DbContext Pooling & SnakeCase Column Naming**: `PlatformSaveChangesInterceptor` is registered as a stateless `Singleton` with `IHttpContextAccessor`, bypassing external HTTP validation calls to ensure high-performance in-memory auditing (`CreatedBy`, `UpdatedBy`, `UpdatedAt`). `AddPlatformDbContext` uses `AddDbContextPool` with a pool size of 1024 and automatically registers `SnakeCaseNamingOptionsExtension` (`IModelFinalizingConvention`) alongside `ApplySnakeCaseColumnNames` so all `PascalCase` C# entity properties map to `snake_case` PostgreSQL columns.

## 🛡️ Security & Tenant Isolation (TenantValidationMiddleware)
A critical part of `ICOT.Core` is the `TenantValidationMiddleware`. It universally enforces multi-tenancy rules across all microservices before requests even reach the controllers:
- **gRPC Internal Calls Bypass**: gRPC requests (`Content-Type: application/grpc` or `GrpcMethodMetadata`) represent direct inter-service communications and automatically bypass `TenantValidationMiddleware` checks and query injections.
- **x-platform == "true"**: The caller has supreme privileges. They bypass `x-workspace` restrictions and can view/modify any workspace data.
- **x-platform != "true"**: The caller is a tenant.
  - The `x-workspace` header MUST be present and valid. If missing, the middleware returns `403 Forbidden`.
  - The middleware **dynamically injects** `workspaceId` and `filter[workspaceId]` into the Request's QueryString using the value from the `x-workspace` header. This automatically forces any `Index` endpoints (or `JsonApiQueryOptions` filters) to be strictly scoped to the tenant's workspace.

## ⚠️ Development Rules (MUST READ)

1. **NuGet Versioning**: All microservices reference this package using wildcards:
   ```xml
   <PackageReference Include="ICOT.Core" Version="*" />
   ```
   **CRITICAL**: NEVER change this to a hardcoded version (e.g., `1.0.8`) in the microservice `.csproj` files.

2. **Publishing Updates (v1.x Automated Rollover)**:
   When you make changes to `ICOT.Core`, the package version increments automatically in `publish.yml` under the `1.x.y` track.
   - Patch increments on each build: `1.0.1` -> `1.0.2` -> ... -> `1.0.14`.
   - When patch reaches 15, minor increments by 1 and patch resets to 1: `1.0.14` -> `1.1.1` -> ... -> `1.1.14` -> `1.2.1`.
   Do NOT manually edit `<Version>` in `ICOT.Core.csproj`.

3. **Global Impact**: Any change made to `ICOT.Core` instantly impacts ALL microservices. Always double-check nullability, dependencies, and performance before modifying core middlewares or filters.

4. **Strict SOLID Principles Compliance (MANDATORY RULE)**:
   From version 1.0.0 onwards, all code inside `ICOT.Core` strictly enforces the five SOLID principles:
   - **Single Responsibility Principle (SRP)**: Every class, middleware, filter, and behavior MUST have only one reason to change. Data sanitization (`ILogPayloadSanitizer`), transport publishing (`IPlatformErrorPublisher`), metadata extraction (`IPaginationMetadataExtractor`), and exception mapping (`IExceptionResponseMapper`) must remain in dedicated single-purpose classes.
   - **Open / Closed Principle (OCP)**: Components must be open for extension but closed for modification. Use strategy abstractions (e.g., `IExceptionResponseMapper`, `ITenantPolicyValidator`) rather than hardcoded conditional branches.
   - **Liskov Substitution Principle (LSP)**: Implementations must be fully substitutable for their abstractions without contract breakage (e.g. `IUserContextProvider` substitutable in background workers and tests).
   - **Interface Segregation Principle (ISP)**: Clients must never be forced to depend on interfaces they do not use (e.g. segregated `IWorkspaceValidationService` and `IAccountValidationService`).
   - **Dependency Inversion Principle (DIP)**: High-level modules must never depend on low-level implementation details or framework-specific statics. Both must depend on abstractions (e.g., `IUserContextProvider`, `IEncryptionService`, `IPlatformErrorPublisher`).
   - **STRICT MANDATE**: For EVERY future change, addition, refactor, or deletion in `ICOT.Core`, the agent MUST audit the changes against SOLID principles. If ANY violation is detected, it MUST be resolved and refactored before release.

- **Standardized HTTP Status Codes**: All API controllers MUST adhere to the following network HTTP status codes:
  - Read All (GET): `200 OK`
  - Read One (GET): `200 OK`
  - Create (POST): `201 Created`
  - Full Update (PUT): `200 OK` or `204 No Content`
  - Partial Update (PATCH): `200 OK` or `204 No Content`
  - Delete (DELETE): `200 OK` or `204 No Content`
  Use `StatusCode(201, ...)` for creations.

- **JSON:API Query Standard (Modular Architecture)**: All `index`/`list` endpoints MUST use `[FromQuery] JsonApiQueryOptions options` from `ICOT.Core.JsonApi` and utilize `.ApplyJsonApiAsync(options)` in their CQRS Handlers to support standardized JSON:API querying, filtering, sorting, and pagination.
  - Architecture & SRP Compliance: `JsonApiExtensions.cs` acts as a clean, lightweight facade (~70 lines) delegating specialized responsibilities to dedicated single-purpose classes:
    - `JsonApiSoftDeleteBuilder.cs`: Encapsulates soft-delete filtering. Inspects entity for nullable `DeletedAt` property. Enforces `deleted_at IS NULL` by default across all `Index` endpoints; when `?trash=true` is requested, filters `deleted_at IS NOT NULL` (trash/recycle bin only); when `?trash=all`, returns both active and deleted records without filtering. Honors explicit `filter[deleted_at]` if provided.
    - `JsonApiFilterBuilder.cs`: Dynamic LINQ expression trees, relation/navigation property filtering (`filter[relation.property]`, 1:1, N:1, and 1:N `.Any(...)`), multilingual JSONB dictionary search (`ILIKE`), null comparisons (`null`/`!null`), and `$or` parameter parsing.
    - `JsonApiSortBuilder.cs`: Sort field validation against entity properties, direction parsing (`-`), and fallback ordering (`CreatedAt desc` / `Id desc`).
    - `JsonApiDataShaper.cs`: Dynamic payload reflection, inclusion/exclusion (`?select`), envelope traversal, nested dot-notation, and `[JsonPropertyName]` preservation.
  - NEVER use manual parameters like `int page` or `int per_page` in controller signatures. 
  - Pagination parameters must automatically be processed as `page[number]` and `page[size]` by `JsonApiQueryOptions`.
  - Soft Delete / Trash Query Parameter: `[FromQuery(Name = "trash")] public string? Trash` in `JsonApiQueryOptions` controls soft-delete mode across all `index` endpoints (`trash=true`, `trash=false`, `trash=all`). All entities inheriting `BaseEntity` are automatically protected against leaking deleted records in index queries.
  - Filtering natively supports `null` / `!null` (e.g. `filter[parent_id]=null`, `filter[parent_id]=!null`) for nullable value types (`Guid?`, `int?`, etc.) and reference types.
  - Multilingual & JSONB Dictionary Filtering: `ApplyJsonApiAsync` natively supports filtering on `Dictionary<string, string>` and `IDictionary` properties (e.g. `Title`, `Description`, `Meta`, `Rules`). It translates to case-insensitive `ILIKE` on PostgreSQL `(field::text)` matching GIN trigram indexes for whole-JSON search (`filter[title]=value`), and supports language/key-specific lookups (`filter[title.fa]=value` or `filter[title][fa]=value`), exact matches (`==`), multiple OR terms (`filter[title]=val1|val2`), and seamless inclusion in global `$or` clauses (e.g. `filter[$or]=slug:iran|iataCode:iran|title:iran|title.en:iran|title.fa:iran`). In-memory evaluation is provided for unit testing.
  - Dynamic Field Selection & Shaping (`?select`): Managed globally by `ApiResponseFilter` and `JsonApiExtensions.ShapeData` (delegating to `JsonApiDataShaper`). Supports inclusion (`?select=id,service`), exclusion (`?select=-meta,-message`), and nested dot-notation (`?select=profile,-profile.age`), seamlessly handling data envelope dictionaries (`{"error_logs": [...]}`), individual DTOs, and direct collections with case-insensitivity, snake_case conversion, and `[JsonPropertyName]` preservation.

### Mandatory Logging
- Every MediatR Command or Query must implement `ICOT.Core.Interfaces.ILoggableRequest` to auto-publish `log:add` to RabbitMQ. Specify `LogService`, `LogTable`, `LogModel`, and `LogType` (`command` or `query`).

### 🚨 Centralized Platform Error Logging (`log:error`)
- **Event Contract (`LogErrorEvent`)**: Defined in `ICOT.Core.Events.LogErrorEvent`. Uses `Event = "log:error"` with lowest priority (`Priority = 1`), dispatched directly to `ACTION_SERVICE_QUEUE` (`icot_action_queue`).
- **HTTP Exception Interception**: `GlobalExceptionHandler` intercepts all unhandled HTTP exceptions across all microservices, formats the response, and asynchronously fire-and-forgets a `LogErrorEvent` containing the originating service name, exception message, HTTP method, path, query string, trace ID, headers (`x-account`, `x-workspace`), and stack trace.
- **Queue Consumer Exception Interception**: `PlatformConsumeErrorObserver` is automatically registered in `AddPlatformRabbitMq`. It listens to `ConsumeFault<T>` across all MassTransit consumers and publishes a `LogErrorEvent` with the faulted consumer type, message type, and exception details, safely skipping recursion if `T` is `LogErrorEvent`.

### 🔔 Centralized Platform Notification Publishing (`notification:add`)
- **Event Contract (`NotificationAddEvent`)**: Defined in `ICOT.Core.Events.NotificationAddEvent` and `NotificationAddEventData`. Uses `Event = "notification:add"` with priority 1, dispatched to `ACTION_SERVICE_QUEUE` (`icot_action_queue`).
- **Notification Type Enum (`NotificationType`)**: Defined in `ICOT.Core.Utils.Enums.NotificationType` (`info`, `warning`, `danger`, `success`).
- **Publisher Abstraction (`IPlatformNotificationPublisher`)**: Defined in `ICOT.Core.Messaging.Services.IPlatformNotificationPublisher` and implemented by `MassTransitNotificationPublisher`. Registered as a Singleton in `AddPlatformApiStandard`. Microservices can inject `IPlatformNotificationPublisher` to easily dispatch user/workspace notifications without coupling to queue topologies or manual message construction.

### 📥 Centralized Platform Queue Execution Publishing (`queue:add`)
- **Event Contract (`QueueAddEvent`)**: Defined in `ICOT.Core.Events.QueueAddEvent` and `QueueAddEventData`. Uses `Event = "queue:add"` with priority 2, dispatched to `ACTION_SERVICE_QUEUE` (`icot_action_queue`).
- **Publisher Abstraction (`IPlatformQueuePublisher`)**: Defined in `ICOT.Core.Messaging.Services.IPlatformQueuePublisher` and implemented by `MassTransitQueuePublisher`. Registered as a Singleton in `AddPlatformApiStandard`. Any microservice can inject `IPlatformQueuePublisher` to delegate asynchronous HTTP actions and long-running operations to `action-service` without constructing low-level bus messages.

### ⚡ Centralized Platform WebSocket Messaging (`ws:send`)
- **Event Contract (`WebSocketSendEvent`)**: Defined in `ICOT.Core.Events.WebSocketSendEvent` and `WebSocketSendEventData`. Uses `Event = "ws:send"` with default priority 2, dispatched to `API_GATEWAY_QUEUE` (`icot_gateway_queue`).
- **Publisher Abstraction (`IPlatformWebSocketPublisher`)**: Defined in `ICOT.Core.Messaging.Services.IPlatformWebSocketPublisher` and implemented by `MassTransitWebSocketPublisher`. Registered as a Singleton in `AddPlatformApiStandard`. Any microservice can inject `IPlatformWebSocketPublisher` to dispatch real-time WebSocket push messages to specific workspaces, private/public rooms, accounts, or signatures.
- **WebSocket HTTP Enum (`PlatformHttpMethod.Ws` / `WS`)**: Added `Ws` and alias `WS = Ws` to `PlatformHttpMethod` in `ICOT.Core.Utils.Enums`. Allows controller WebSocket endpoints to declare `[RegistryEndpoint(PlatformHttpMethod.Ws, ...)]`, enabling load/gather endpoint generators to dynamically emit `"method": "ws"`, `"protocol": "wss://"` (for HTTPS) or `"ws://"` (for HTTP), and corresponding routes.

### 📶 RabbitMQ Priority Queues & AMQP Transport-Level Priority (`x-max-priority = 10`) (2026-11-09)
- **Automatic Priority Queue Declaration**: In `RabbitMqExtensions.AddPlatformMassTransit`, registered `x.AddConfigureEndpointsCallback` on `IBusRegistrationConfigurator`. Every RabbitMQ receive endpoint across all microservices is automatically configured with `rmq.EnablePriority(10)` (`x-max-priority = 10`).
- **Exchange-Based Send Endpoints (`exchange:{queueName}`)**: Updated all platform publishers (`MassTransitWebSocketPublisher`, `MassTransitQueuePublisher`, `MassTransitNotificationPublisher`, `MassTransitErrorPublisher`, and `LogAddBehavior`) to publish to `new Uri($"exchange:{queueName}")` instead of `queue:{queueName}`. In RabbitMQ, sending to `queue:` causes MassTransit to issue `QueueDeclare` with empty arguments, triggering `406 PRECONDITION_FAILED` against queues created with `x-max-priority`. Publishing directly to `exchange:{queueName}` avoids queue declaration on the send side while preserving message routing into the priority queue and keeping AMQP message priority intact.
- **Transport-Level AMQP Priority Injection**: Updated all platform publishers (`MassTransitWebSocketPublisher`, `MassTransitQueuePublisher`, `MassTransitNotificationPublisher`, `MassTransitErrorPublisher`, and `LogAddBehavior`) to explicitly set the AMQP basic properties priority (`ctx => ctx.SetPriority((byte)Math.Clamp(event.Priority, 0, 10))`) on `sendEndpoint.Send`.
- **Priority Hierarchy**:
  - `QueueAddEvent` (`queue:add`): Priority 2 (processed ahead of bulk logs)
  - `WebSocketSendEvent` (`ws:send`): Priority 2 (real-time push notifications delivered ahead of bulk logging)
  - `LogAddEvent` (`log:add`), `LogErrorEvent` (`log:error`), `RequestAddEvent` (`request:add`): Priority 1 (lowest priority background logging)
- **Queue Recreation Notice**: Because queue arguments are immutable in RabbitMQ, when deploying this update, existing empty queues (`icot_action_queue`, `icot_gateway_queue`, etc.) must be deleted or re-declared once so RabbitMQ creates them with the `x-max-priority: 10` argument.

### 🚀 Single-Port Kestrel Hosting & Inter-Service gRPC Architecture
- **Single-Port Architecture**: Every microservice listens on a single port specified by `<SERVICE>_SERVICE_URL` (e.g. `http://localhost:5005`). No dual ports, no `port + 100`, and no separate `_GRPC_PORT` or `_GRPC_URL` variables.
- **Kestrel Protocol Setup (`ConfigurePlatformHost`)**:
  - **Backend Services**: Configured with `HttpProtocols.Http2` on the service port. Both gRPC endpoints and HTTP REST endpoints are hosted on this single HTTP/2 endpoint.
  - **API Gateway (`gateway`)**: Configured with `HttpProtocols.Http1AndHttp2` to accept both HTTP/1.1 and HTTP/2 from external clients, while forwarding requests to backend microservices over HTTP/2 using YARP (`proxyRequest.Version = HttpVersion.Version20; proxyRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;`).
- **Inter-Service gRPC Resolution (`AddPlatformGrpcClient<TClient>`)**:
  - Enables `SocketsHttpHandler.Http2UnencryptedSupport` via AppContext switch.
  - Discovers target endpoint directly from `<SERVICE>_SERVICE_URL` without any port manipulation.
  - Configures `GrpcClient` to target the service URL directly using HTTP/2 cleartext.
  - **High-Performance Connection Pooling & Lifecycle Tuning**:
    - Configured `.SetHandlerLifetime(TimeSpan.FromHours(1))` and `PooledConnectionLifetime = TimeSpan.FromHours(1)` to eliminate the 2-minute connection churn and cold start latency spikes.
    - Configured `EnableMultipleHttp2Connections = true` on `SocketsHttpHandler` to scale out across parallel HTTP/2 TCP sockets during traffic bursts, preventing Head-of-Line blocking.
    - Configured `KeepAlivePingDelay = 60s`, `KeepAlivePingTimeout = 30s`, and `KeepAlivePingPolicy = Always` to keep connections permanently warm and prevent silent drops by cloud NAT, K8s, or firewalls.

### 📦 Self-Contained Proto Contract Architecture (Docker & CI Deployment Rule)
- **Zero Cross-Repository References**: In Docker and CI environments, each microservice builds inside an isolated build context containing only its own repository. NEVER reference `.proto` files using relative cross-repository paths (e.g. `..\..\..\other-service\...`).
- **Local Contract Placement**:
  - **Service Provider (Server)**: Hosts contract in `src/{Service}.API/Protos/{name}.proto` with `<Protobuf Include="Protos\{name}.proto" GrpcServices="Server" />`.
  - **Service Consumer (Client)**: Copies the contract locally into `src/{Service}.Infrastructure/Protos/{name}.proto` with `<Protobuf Include="Protos\{name}.proto" GrpcServices="Client" />`.

## Naming & Casing Standard

| بخش | Convention استاندارد | مثال |
| --- | --- | --- |
| نام کلاس‌ها | `PascalCase` | `UserService` |
| نام متدها | `PascalCase` | `GetUserById()` |
| نام Propertyها | `PascalCase` | `FirstName` |
| نام متغیرها | `camelCase` | `userId` |
| پارامترهای متد | `camelCase` | `workspaceId` |
| فیلدهای خصوصی | `camelCase` با `_` | `_userRepository` |
| نام فایل‌های C# | `PascalCase` | `UserService.cs` |
| نام پروژه‌ها | `PascalCase` | `User.Application.csproj` |
| نام JSON در API | `camelCase` | `firstName` |
| نام جداول دیتابیس | `snake_case` | `users` |
| نام ستون‌های دیتابیس | `snake_case` | `first_name` |
| نام Migrationها | `PascalCase` | `CreateUsersTable` |
| نام پوشه‌ها | `PascalCase` | `Users` |

- **API Routes**: `kebab-case` (e.g., `/api/v1/tour/tour-event-types`, `/api/v1/customer/user-experiences`, `/api/v1/workspace/account-roles`).
- **JSON Payloads, Envelopes, Rules, Meta, Filters & Pagination**: Strictly `camelCase` across all requests, responses, `rules`/`meta` dictionaries, query filters (`filter[workspaceId]`), pagination metadata (`perPage`, `currentPage`, `lastPage`, `nextPageUrl`, `prevPageUrl`), and RabbitMQ events. No `snake_case` fallbacks (`IJsonOnDeserialized` / `JsonExtensionData`) are used.
- **Database Tables & Columns (PostgreSQL)**: Strictly `snake_case` enforced via `builder.ApplySnakeCaseColumnNames()` in `OnModelCreating`.
