using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using HPK.Core.Utils.Responses;
using HPK.Core.Middlewares;

namespace HPK.Core.Filters;

/// <summary>
/// Enforces the universal tenant rule at the MVC action level.
/// Delegates evaluation to <see cref="ITenantPolicyValidator"/> (DRY & SRP).
/// </summary>
public class PlatformAuthorizationFilter : IAsyncActionFilter
{
    private readonly ITenantPolicyValidator _policyValidator;

    public PlatformAuthorizationFilter(ITenantPolicyValidator? policyValidator = null)
    {
        _policyValidator = policyValidator ?? DefaultTenantPolicyValidator.Instance;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var result = _policyValidator.Validate(context.HttpContext);

        if (result.IsBypassed)
        {
            await next();
            return;
        }

        if (!result.IsAllowed)
        {
            var response = ApiResponse<object>.Error(403, result.FailureReason ?? "Forbidden: Missing or invalid x-workspace header for tenant request.");
            context.Result = new ObjectResult(response) { StatusCode = 403 };
            return;
        }

        await next();
    }
}
