using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using HPK.Core.Utils.Responses;

namespace HPK.Core.Middlewares;

public class TenantValidationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Bypass for gRPC internal communication
        if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true ||
            context.GetEndpoint()?.Metadata.GetMetadata<Grpc.AspNetCore.Server.GrpcMethodMetadata>() != null)
        {
            await _next(context);
            return;
        }

        // Bypass if the API Gateway resolved this as a public endpoint (authentication = false)
        if (context.Request.Headers["x-authentication"] == "false")
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        // Bypass for internal APIs, swagger docs, etc.
        if (path.Contains("/internal/") || path.Contains("/docs") || path.Contains("/openapi"))
        {
            await _next(context);
            return;
        }

        var platformHeader = context.Request.Headers["x-platform"].ToString();
        var isPlatform = platformHeader.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!isPlatform)
        {
            var workspaceHeader = context.Request.Headers["x-workspace"].ToString();
            bool hasValidWorkspace = !string.IsNullOrWhiteSpace(workspaceHeader) && Guid.TryParse(workspaceHeader, out _);

            // Strict check: if not a platform request, workspace is MANDATORY.
            // Exception: /auth/identify and /auth/login/password can fallback to workspaceCode in payload.
            bool isWorkspaceOptionalRoute = path.Contains("/auth/identify") || path.Contains("/auth/login/password");

            if (!hasValidWorkspace && !isWorkspaceOptionalRoute)
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                var response = ApiResponse<object>.Error(403, "Forbidden: Missing or invalid x-workspace header for tenant request.");
                await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return;
            }

            // Only inject if the workspace header is present and valid
            if (hasValidWorkspace)
            {
                // Inject the workspace constraints into the query string for backend endpoints
                // This safely forces JsonApiQueryOptions and [FromQuery] params to be scoped to the tenant's workspace
                var queryDict = QueryHelpers.ParseQuery(context.Request.QueryString.Value);
                
                var updatedQuery = queryDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                
                // Forcefully override/inject the workspace filtering
                updatedQuery["workspaceId"] = new Microsoft.Extensions.Primitives.StringValues(workspaceHeader);
                updatedQuery["filter[workspace_id]"] = new Microsoft.Extensions.Primitives.StringValues(workspaceHeader);

                var qb = new QueryBuilder(updatedQuery);
                context.Request.QueryString = qb.ToQueryString();
            }
        }

        await _next(context);
    }
}
