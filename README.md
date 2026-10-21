# ICOT Shared Packages (icot-utils)

This repository contains the core `ICOT.Core` library used across the ICOT Microservices ecosystem. 

This package is designed to provide **infrastructural building blocks** and cross-cutting concerns (authentication, API standardization, database extensions, and raw messaging pipelines).

### 🏛️ Architectural Principles

1. **No Domain Logic**: These packages must never contain business logic, domain models, or service-specific interfaces.
2. **No Shared Event Models**: Do not put event definitions (e.g., `CustomerAddEventData`, `AccountPermissionSyncEvent`, `MessengerEventMessage`) in these libraries. Event contracts must be defined and owned by the individual microservices. By utilizing MassTransit's Raw JSON Serialization, services can publish anonymous objects and consume them into local representations without needing a shared assembly.
3. **Pluggable & Extensible**: Components here should act as middleware or extension methods that standard ASP.NET Core applications can plug into seamlessly.

### 📦 Packages

* **`ICOT.Core`**: Core enums, JSON API models, standard API responses, filters, routing extensions, EF Core database extensions, global error handling, and base configuration for MassTransit/RabbitMQ.

### 📝 OpenAPI & Documentation
In .NET 9, `ICOT.Core` configures the OpenAPI specification manually using a custom `IOpenApiOperationTransformer`. Because standard `.NET 9` OpenAPI generation does not automatically parse XML Comments for MVC Controllers, developers **must** use `[EndpointSummary("...")]` and `[EndpointDescription("...")]` (from the `Microsoft.AspNetCore.Http` namespace) to document Controller action methods. The custom transformer in `ICOT.Core` will parse these and display them correctly in the Scalar UI.
