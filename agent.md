# Agent Knowledge Base: icot-nuget

This file serves as the definitive reference for any AI agent interacting with the `icot-nuget` directory. `icot-nuget` contains the core shared libraries used across all 13+ microservices in the ICOT platform.

## 📦 Architecture & Packages
The directory contains the primary NuGet package:

### 1. HPK.Core
The backbone of the microservice architecture. It provides standardized behaviors, middlewares, and extensions so microservices don't have to rewrite boilerplate.
- **Base Entities**: Standardized base classes for EF Core (e.g., `BaseEntity`).
- **Standardized Responses**: The `ApiResponse<T>` wrapper ensures all microservices return exactly the same JSON structure.
- **Global Error Handling**: `GlobalExceptionHandler` automatically intercepts exceptions and formats them into an `ApiResponse`.
- **OpenAPI & Scalar Docs**: `PlatformApiExtensions.cs` configures OpenAPI. It includes an `OperationTransformer` that globally injects `x-account`, `x-language`, and `x-workspace` as default header parameters into all endpoints across all microservices. **Crucially, because .NET 9's Microsoft.AspNetCore.OpenApi does not natively map XML comments or Minimal API summary attributes to MVC Controllers**, this transformer explicitly extracts `[EndpointSummary("...")]` and `[EndpointDescription("...")]` (from `Microsoft.AspNetCore.Http`) and applies them to the OpenAPI schema. When documenting endpoints, agents **MUST** use these attributes instead of C# XML comments (`///`).
- **Proxy Configuration**: Configures `ForwardedHeadersOptions` to trust the internal API Gateway (YARP), ensuring Swagger generates correct HTTPS/Host URLs.
- **Messaging**: Handles inter-service communication using MassTransit and RabbitMQ.

## 🛡️ Security & Tenant Isolation (TenantValidationMiddleware)
A critical part of `HPK.Core` is the `TenantValidationMiddleware`. It universally enforces multi-tenancy rules across all microservices before requests even reach the controllers:
- **x-platform == "true"**: The caller has supreme privileges. They bypass `x-workspace` restrictions and can view/modify any workspace data.
- **x-platform != "true"**: The caller is a tenant.
  - The `x-workspace` header MUST be present and valid. If missing, the middleware returns `403 Forbidden`.
  - The middleware **dynamically injects** `workspaceId` and `filter[workspace_id]` into the Request's QueryString using the value from the `x-workspace` header. This automatically forces any `Index` endpoints (or `JsonApiQueryOptions` filters) to be strictly scoped to the tenant's workspace.

## ⚠️ Development Rules (MUST READ)

1. **NuGet Versioning**: All microservices reference this package using wildcards:
   ```xml
   <PackageReference Include="HPK.Core" Version="*" />
   ```
   **CRITICAL**: NEVER change this to a hardcoded version (e.g., `1.0.8`) in the microservice `.csproj` files.

2. **Publishing Updates**:
   When you make changes to `HPK.Core`, the package version goes up automatically. Do NOT manually change `<Version>` in `HPK.Core.csproj`.

3. **Global Impact**: Any change made to `HPK.Core` instantly impacts ALL microservices. Always double-check nullability, dependencies, and performance before modifying core middlewares or filters.

- **Standardized HTTP Status Codes**: All API controllers MUST adhere to the following network HTTP status codes:
  - Read All (GET): `200 OK`
  - Read One (GET): `200 OK`
  - Create (POST): `201 Created`
  - Full Update (PUT): `200 OK` or `204 No Content`
  - Partial Update (PATCH): `200 OK` or `204 No Content`
  - Delete (DELETE): `200 OK` or `204 No Content`
  Use `StatusCode(201, ...)` for creations.

- **JSON:API Query Standard**: All `index`/`list` endpoints MUST use `[FromQuery] JsonApiQueryOptions options` from `HPK.Core.JsonApi` and utilize `.ApplyJsonApiAsync(options)` in their CQRS Handlers to support standardized JSON:API querying, filtering, sorting, and pagination. 
  - NEVER use manual parameters like `int page` or `int per_page` in controller signatures. 
  - Pagination parameters must automatically be processed as `page[number]` and `page[size]` by `JsonApiQueryOptions`.
  - Filtering natively supports `null` / `!null` (e.g. `filter[parent_id]=null`, `filter[parent_id]=!null`) for nullable value types (`Guid?`, `int?`, etc.) and reference types.

### Mandatory Logging
- Every MediatR Command or Query must implement `HPK.Core.Interfaces.ILoggableRequest` to auto-publish `log:add` to RabbitMQ. Specify `LogService`, `LogTable`, `LogModel`, and `LogType` (`command` or `query`).
