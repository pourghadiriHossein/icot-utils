using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using HPK.Core.Utils.Responses;

namespace HPK.Core.Filters;

/// <summary>
/// Enforces the universal rule:
/// - If x-platform != true, x-workspace header MUST be present and valid GUID.
/// - Exceptions are /auth/identify and /auth/login/password which fallback to workspaceCode in payload.
/// </summary>
public class PlatformAuthorizationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        
        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            await next();
            return;
        }

        if (request.Headers["x-authentication"] == "false")
        {
            await next();
            return;
        }

        var path = request.Path.Value ?? string.Empty;

        // Skip internal or docs endpoints
        if (path.Contains("/internal/") || path.Contains("/docs") || path.Contains("/openapi"))
        {
            await next();
            return;
        }

        var platformHeader = request.Headers["x-platform"].ToString();
        var isPlatform = platformHeader.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!isPlatform)
        {
            var workspaceHeader = request.Headers["x-workspace"].ToString();
            bool hasValidWorkspace = !string.IsNullOrWhiteSpace(workspaceHeader) && Guid.TryParse(workspaceHeader, out _);

            bool isWorkspaceOptionalRoute = path.Contains("/auth/identify") || path.Contains("/auth/login/password");

            if (!hasValidWorkspace && !isWorkspaceOptionalRoute)
            {
                var response = ApiResponse<object>.Error(403, "Forbidden: Missing or invalid x-workspace header for tenant request.");
                context.Result = new ObjectResult(response) { StatusCode = 403 };
                return;
            }
        }

        await next();
    }
}
