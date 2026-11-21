using System;
using System.Collections.Generic;
using System.Linq;
using ICOT.Core.Exceptions;
using Microsoft.AspNetCore.Http;

namespace ICOT.Core.Utils.Services;

/// <summary>
/// Stateless utilities for parsing the gateway-injected <c>x-scopes</c> header and enforcing force-delete permissions.
/// </summary>
public static class ScopeHelper
{
    public const string HeaderName = "x-scopes";
    public const string ForceDeleteSuffix = ".force_delete";

    public static IReadOnlySet<string> ParseScopes(string? rawHeader)
    {
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return rawHeader
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool HasScope(string? rawHeader, string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(requiredScope))
        {
            return false;
        }

        return ParseScopes(rawHeader).Contains(requiredScope.Trim());
    }

    public static bool HasScope(HttpRequest? request, string requiredScope)
    {
        if (request == null)
        {
            return false;
        }

        return HasScope(request.Headers[HeaderName].ToString(), requiredScope);
    }

    public static bool HasScope(HttpContext? httpContext, string requiredScope)
    {
        return HasScope(httpContext?.Request, requiredScope);
    }

    public static void EnsureForceDeleteAuthorized(bool force, string? rawScopesHeader, string requiredForceScope)
    {
        if (!force)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(requiredForceScope))
        {
            throw new ForbiddenAccessException("Forbidden: Required force-delete permission scope is not configured.");
        }

        var normalizedScope = requiredForceScope.Trim();
        if (!HasScope(rawScopesHeader, normalizedScope))
        {
            throw new ForbiddenAccessException(
                $"Forbidden: Missing required force-delete permission scope '{normalizedScope}'.");
        }
    }

    public static void EnsureForceDeleteAuthorized(bool force, HttpContext? httpContext, string requiredForceScope)
    {
        EnsureForceDeleteAuthorized(
            force,
            httpContext?.Request.Headers[HeaderName].ToString(),
            requiredForceScope);
    }
}

/// <summary>
/// Segregated abstraction for accessing and validating endpoint scopes injected via <c>x-scopes</c> (ISP &amp; DIP).
/// </summary>
public interface IScopeContextProvider
{
    IReadOnlySet<string> CurrentScopes { get; }
    bool HasScope(string requiredScope);
    void EnsureForceDeleteAuthorized(bool force, string requiredForceScope);
}

/// <summary>
/// Default implementation of <see cref="IScopeContextProvider"/> backed by <see cref="IHttpContextAccessor"/>.
/// </summary>
public class HttpContextScopeContextProvider : IScopeContextProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextScopeContextProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public IReadOnlySet<string> CurrentScopes =>
        ScopeHelper.ParseScopes(_httpContextAccessor.HttpContext?.Request.Headers[ScopeHelper.HeaderName].ToString());

    public bool HasScope(string requiredScope) =>
        ScopeHelper.HasScope(_httpContextAccessor.HttpContext, requiredScope);

    public void EnsureForceDeleteAuthorized(bool force, string requiredForceScope) =>
        ScopeHelper.EnsureForceDeleteAuthorized(force, _httpContextAccessor.HttpContext, requiredForceScope);
}
