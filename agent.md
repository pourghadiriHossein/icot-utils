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
- **EF Core Interceptors & DbContext Pooling**: `PlatformSaveChangesInterceptor` is registered as a stateless `Singleton` with `IHttpContextAccessor`, bypassing external HTTP validation calls to ensure high-performance in-memory auditing (`CreatedBy`, `UpdatedBy`, `UpdatedAt`). `AddPlatformDbContext` uses `AddDbContextPool` with a pool size of 1024, compatible with design-time EF CLI tooling and root service providers.

## 🛡️ Security & Tenant Isolation (TenantValidationMiddleware)
A critical part of `ICOT.Core` is the `TenantValidationMiddleware`. It universally enforces multi-tenancy rules across all microservices before requests even reach the controllers:
- **gRPC Internal Calls Bypass**: gRPC requests (`Content-Type: application/grpc` or `GrpcMethodMetadata`) represent direct inter-service communications and automatically bypass `TenantValidationMiddleware` checks and query injections.
- **x-platform == "true"**: The caller has supreme privileges. They bypass `x-workspace` restrictions and can view/modify any workspace data.
- **x-platform != "true"**: The caller is a tenant.
  - The `x-workspace` header MUST be present and valid. If missing, the middleware returns `403 Forbidden`.
  - The middleware **dynamically injects** `workspaceId` and `filter[workspace_id]` into the Request's QueryString using the value from the `x-workspace` header. This automatically forces any `Index` endpoints (or `JsonApiQueryOptions` filters) to be strictly scoped to the tenant's workspace.

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
  - Architecture & SRP Compliance: `JsonApiExtensions.cs` acts as a clean, lightweight facade (~60 lines) delegating specialized responsibilities to dedicated single-purpose classes:
    - `JsonApiFilterBuilder.cs`: Dynamic LINQ expression trees, multilingual JSONB dictionary search (`ILIKE`), null comparisons (`null`/`!null`), and `$or` parameter parsing.
    - `JsonApiSortBuilder.cs`: Sort field validation against entity properties, direction parsing (`-`), and fallback ordering (`CreatedAt desc` / `Id desc`).
    - `JsonApiDataShaper.cs`: Dynamic payload reflection, inclusion/exclusion (`?select`), envelope traversal, nested dot-notation, and `[JsonPropertyName]` preservation.
  - NEVER use manual parameters like `int page` or `int per_page` in controller signatures. 
  - Pagination parameters must automatically be processed as `page[number]` and `page[size]` by `JsonApiQueryOptions`.
  - Filtering natively supports `null` / `!null` (e.g. `filter[parent_id]=null`, `filter[parent_id]=!null`) for nullable value types (`Guid?`, `int?`, etc.) and reference types.
  - Multilingual & JSONB Dictionary Filtering: `ApplyJsonApiAsync` natively supports filtering on `Dictionary<string, string>` and `IDictionary` properties (e.g. `Title`, `Description`, `Meta`, `Rules`). It translates to case-insensitive `ILIKE` on PostgreSQL `(field::text)` matching GIN trigram indexes for whole-JSON search (`filter[title]=value`), and supports language/key-specific lookups (`filter[title.fa]=value` or `filter[title][fa]=value`), exact matches (`==`), multiple OR terms (`filter[title]=val1|val2`), and seamless inclusion in global `$or` clauses (e.g. `filter[$or]=slug:iran|iataCode:iran|title:iran|title.en:iran|title.fa:iran`). In-memory evaluation is provided for unit testing.
  - Dynamic Field Selection & Shaping (`?select`): Managed globally by `ApiResponseFilter` and `JsonApiExtensions.ShapeData` (delegating to `JsonApiDataShaper`). Supports inclusion (`?select=id,service`), exclusion (`?select=-meta,-message`), and nested dot-notation (`?select=profile,-profile.age`), seamlessly handling data envelope dictionaries (`{"error_logs": [...]}`), individual DTOs, and direct collections with case-insensitivity, snake_case conversion, and `[JsonPropertyName]` preservation.

### Mandatory Logging
- Every MediatR Command or Query must implement `ICOT.Core.Interfaces.ILoggableRequest` to auto-publish `log:add` to RabbitMQ. Specify `LogService`, `LogTable`, `LogModel`, and `LogType` (`command` or `query`).

### 🚨 Centralized Platform Error Logging (`log:error`)
- **Event Contract (`LogErrorEvent`)**: Defined in `ICOT.Core.Events.LogErrorEvent`. Uses `Event = "log:error"` with lowest priority (`Priority = 1`), dispatched directly to `ACTION_SERVICE_QUEUE` (`icot_action_queue`).
- **HTTP Exception Interception**: `GlobalExceptionHandler` intercepts all unhandled HTTP exceptions across all microservices, formats the response, and asynchronously fire-and-forgets a `LogErrorEvent` containing the originating service name, exception message, HTTP method, path, query string, trace ID, headers (`x-account`, `x-workspace`), and stack trace.
- **Queue Consumer Exception Interception**: `PlatformConsumeErrorObserver` is automatically registered in `AddPlatformRabbitMq`. It listens to `ConsumeFault<T>` across all MassTransit consumers and publishes a `LogErrorEvent` with the faulted consumer type, message type, and exception details, safely skipping recursion if `T` is `LogErrorEvent`.

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
