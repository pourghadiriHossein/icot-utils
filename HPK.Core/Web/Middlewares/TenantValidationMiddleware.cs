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
    private readonly ITenantPolicyValidator _defaultValidator;

    public TenantValidationMiddleware(RequestDelegate next, ITenantPolicyValidator? defaultValidator = null)
    {
        _next = next;
        _defaultValidator = defaultValidator ?? DefaultTenantPolicyValidator.Instance;
    }

    public async Task InvokeAsync(HttpContext context, ITenantPolicyValidator? scopedValidator = null)
    {
        var validator = scopedValidator ?? _defaultValidator;
        var result = validator.Validate(context);

        if (result.IsBypassed)
        {
            await _next(context);
            return;
        }

        if (!result.IsAllowed)
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var response = ApiResponse<object>.Error(403, result.FailureReason ?? "Forbidden: Missing or invalid x-workspace header for tenant request.");
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return;
        }

        // Only inject if workspace header is present and valid
        if (result.HasValidWorkspace && !string.IsNullOrWhiteSpace(result.WorkspaceHeader))
        {
            // Inject the workspace constraints into the query string for backend endpoints
            // This safely forces JsonApiQueryOptions and [FromQuery] params to be scoped to the tenant's workspace
            var queryDict = QueryHelpers.ParseQuery(context.Request.QueryString.Value);
            var updatedQuery = queryDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            
            // Forcefully override/inject the workspace filtering
            updatedQuery["workspaceId"] = new Microsoft.Extensions.Primitives.StringValues(result.WorkspaceHeader);
            updatedQuery["filter[workspace_id]"] = new Microsoft.Extensions.Primitives.StringValues(result.WorkspaceHeader);

            var qb = new QueryBuilder(updatedQuery);
            context.Request.QueryString = qb.ToQueryString();
        }

        await _next(context);
    }
}
