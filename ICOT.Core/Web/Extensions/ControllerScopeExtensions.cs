using ICOT.Core.Utils.Services;
using Microsoft.AspNetCore.Mvc;

namespace ICOT.Core.Extensions;

/// <summary>
/// Controller helper extensions for reading and validating endpoint scopes from the <c>x-scopes</c> request header.
/// </summary>
public static class ControllerScopeExtensions
{
    public static bool HasScope(this ControllerBase controller, string requiredScope)
    {
        return ScopeHelper.HasScope(controller?.HttpContext, requiredScope);
    }

    public static void EnsureForceDeleteAuthorized(this ControllerBase controller, bool force, string requiredForceScope)
    {
        ScopeHelper.EnsureForceDeleteAuthorized(force, controller?.HttpContext, requiredForceScope);
    }
}
