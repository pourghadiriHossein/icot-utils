using System;
using Microsoft.AspNetCore.Http;

namespace HPK.Core.Middlewares;

public record TenantValidationResult(
    bool IsAllowed,
    bool IsBypassed,
    bool HasValidWorkspace,
    string? WorkspaceHeader,
    string? FailureReason = null);

/// <summary>
/// Abstraction for multi-tenancy and platform permission validation policies (SRP & OCP).
/// Centralizes tenant boundaries and route bypass evaluation.
/// </summary>
public interface ITenantPolicyValidator
{
    TenantValidationResult Validate(HttpContext context);
}

/// <summary>
/// Default implementation of <see cref="ITenantPolicyValidator"/>.
/// </summary>
public class DefaultTenantPolicyValidator : ITenantPolicyValidator
{
    public static DefaultTenantPolicyValidator Instance { get; } = new();

    public TenantValidationResult Validate(HttpContext context)
    {
        var request = context.Request;

        // 1. Bypass for gRPC internal communication
        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true ||
            context.GetEndpoint()?.Metadata.GetMetadata<Grpc.AspNetCore.Server.GrpcMethodMetadata>() != null)
        {
            return new TenantValidationResult(true, true, false, null);
        }

        // 2. Bypass if API Gateway marked this request as public (x-authentication: false)
        if (request.Headers["x-authentication"] == "false")
        {
            return new TenantValidationResult(true, true, false, null);
        }

        var path = request.Path.Value ?? string.Empty;

        // 3. Bypass for internal APIs, swagger / scalar docs, and openapi
        if (path.Contains("/internal/") || path.Contains("/docs") || path.Contains("/openapi"))
        {
            return new TenantValidationResult(true, true, false, null);
        }

        // 4. Platform check: supreme privileges bypass tenant validation
        var platformHeader = request.Headers["x-platform"].ToString();
        var isPlatform = string.Equals(platformHeader, "true", StringComparison.OrdinalIgnoreCase);

        if (isPlatform)
        {
            return new TenantValidationResult(true, true, false, null);
        }

        // 5. Tenant validation: x-workspace must be a valid GUID unless on optional routes
        var workspaceHeader = request.Headers["x-workspace"].ToString();
        bool hasValidWorkspace = !string.IsNullOrWhiteSpace(workspaceHeader) && Guid.TryParse(workspaceHeader, out _);
        bool isWorkspaceOptionalRoute = path.Contains("/auth/identify") || path.Contains("/auth/login/password");

        if (!hasValidWorkspace && !isWorkspaceOptionalRoute)
        {
            return new TenantValidationResult(
                false, 
                false, 
                false, 
                workspaceHeader, 
                "Forbidden: Missing or invalid x-workspace header for tenant request.");
        }

        return new TenantValidationResult(true, false, hasValidWorkspace, hasValidWorkspace ? workspaceHeader : null);
    }
}
