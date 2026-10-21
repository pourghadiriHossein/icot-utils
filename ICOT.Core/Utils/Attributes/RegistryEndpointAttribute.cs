using System;
using HPK.Core.Utils.Enums;

namespace HPK.Core.Utils.Attributes;

/// <summary>
/// Declarative metadata attribute for automatic endpoint registration, routing, permissions, and scopes across ApiGateway.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class RegistryEndpointAttribute : Attribute
{
    public PlatformHttpMethod Method { get; set; }
    public PlatformService Service { get; set; }
    public string Route { get; set; }
    public string[] Permissions { get; set; }
    public string[] Scopes { get; set; }
    public bool NeedAuthentication { get; set; }
    public bool NeedAuthorization { get; set; }

    public RegistryEndpointAttribute(
        PlatformHttpMethod method,
        PlatformService service,
        string route,
        string[]? permissions = null,
        string[]? scopes = null,
        bool needAuthentication = true,
        bool needAuthorization = true)
    {
        Method = method;
        Service = service;
        Route = route;
        Permissions = permissions ?? Array.Empty<string>();
        Scopes = scopes ?? Array.Empty<string>();
        NeedAuthentication = needAuthentication;
        NeedAuthorization = needAuthorization;
    }

    /// <summary>
    /// Computes the absolute route by prepending the BASE_URL (e.g. http://icot.dev/api/v1/...)
    /// </summary>
    public string GetFullRoute(string? baseUrl = null)
    {
        var baseUri = (baseUrl ?? Environment.GetEnvironmentVariable("BASE_URL") ?? "http://icot.dev").TrimEnd('/');
        var cleanRoute = Route.TrimStart('/');
        return $"{baseUri}/{cleanRoute}";
    }
}
