using System;
using Microsoft.AspNetCore.Http;

namespace HPK.Core.Utils.Services;

/// <summary>
/// Abstraction for accessing current user, tenant, and platform execution context (DIP).
/// Decouples components from low-level IHttpContextAccessor, allowing substitution in CLI/background workers.
/// </summary>
public interface IUserContextProvider
{
    string? CurrentAccountId { get; }
    Guid? CurrentWorkspaceId { get; }
    bool IsPlatform { get; }
}

/// <summary>
/// Default implementation of <see cref="IUserContextProvider"/> extracting context from HTTP request headers.
/// </summary>
public class HttpContextUserContextProvider : IUserContextProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextUserContextProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? CurrentAccountId
    {
        get
        {
            var header = _httpContextAccessor.HttpContext?.Request.Headers["x-account"].ToString();
            return string.IsNullOrWhiteSpace(header) ? null : header;
        }
    }

    public Guid? CurrentWorkspaceId
    {
        get
        {
            var header = _httpContextAccessor.HttpContext?.Request.Headers["x-workspace"].ToString();
            return Guid.TryParse(header, out var wsId) ? wsId : null;
        }
    }

    public bool IsPlatform
    {
        get
        {
            var header = _httpContextAccessor.HttpContext?.Request.Headers["x-platform"].ToString();
            return string.Equals(header, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
