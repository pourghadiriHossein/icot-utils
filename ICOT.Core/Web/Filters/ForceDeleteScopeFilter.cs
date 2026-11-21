using System;
using System.Linq;
using System.Threading.Tasks;
using ICOT.Core.Utils.Attributes;
using ICOT.Core.Utils.Responses;
using ICOT.Core.Utils.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ICOT.Core.Filters;

/// <summary>
/// MVC action filter that enforces <c>.force_delete</c> endpoint scopes from <see cref="RegistryEndpointAttribute.Scopes"/>
/// whenever a DELETE endpoint is invoked with <c>?force=true</c>.
/// </summary>
public class ForceDeleteScopeFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsDelete(request.Method) ||
            !request.Query.TryGetValue("force", out var forceValues) ||
            !bool.TryParse(forceValues.ToString().Trim(), out var isForce) ||
            !isForce)
        {
            await next();
            return;
        }

        var registryAttr = context.ActionDescriptor.EndpointMetadata
            .OfType<RegistryEndpointAttribute>()
            .FirstOrDefault();

        if (registryAttr?.Scopes == null || registryAttr.Scopes.Length == 0)
        {
            await next();
            return;
        }

        var requiredForceScopes = registryAttr.Scopes
            .Where(s => !string.IsNullOrWhiteSpace(s) &&
                        s.Trim().EndsWith(ScopeHelper.ForceDeleteSuffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (requiredForceScopes.Length == 0)
        {
            await next();
            return;
        }

        var missingScope = requiredForceScopes.FirstOrDefault(scope => !ScopeHelper.HasScope(context.HttpContext, scope));
        if (!string.IsNullOrWhiteSpace(missingScope))
        {
            var normalized = missingScope.Trim();
            var response = ApiResponse.Error(
                StatusCodes.Status403Forbidden,
                $"Forbidden: Missing required force-delete permission scope '{normalized}'.");

            context.Result = new ObjectResult(response)
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}
