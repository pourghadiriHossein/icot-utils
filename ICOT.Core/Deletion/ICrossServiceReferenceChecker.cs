using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.ClientFactory;
using ICOT.Core.Extensions;
using ICOT.Core.Grpc.ReferenceCheck;
using Microsoft.Extensions.Logging;

namespace ICOT.Core.Deletion;

/// <summary>
/// Abstraction for reverse-querying downstream microservices over gRPC to discover active dependent tables
/// referencing a record being deleted (DIP &amp; OCP).
/// </summary>
public interface ICrossServiceReferenceChecker
{
    Task<IReadOnlyList<string>> CheckServicesAsync(
        IEnumerable<string> downstreamServices,
        string targetService,
        string targetTable,
        string targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Null-object implementation for services that have no cross-service dependents registered.
/// </summary>
public sealed class NoOpCrossServiceReferenceChecker : ICrossServiceReferenceChecker
{
    public Task<IReadOnlyList<string>> CheckServicesAsync(
        IEnumerable<string> downstreamServices,
        string targetService,
        string targetTable,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}

/// <summary>
/// Concurrent gRPC client orchestrator calling named <see cref="ReferenceCheckGrpcService"/> endpoints across downstream services.
/// Uses fail-closed semantics so unavailable downstream services propagate <see cref="Grpc.Core.RpcException"/> to <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class GrpcCrossServiceReferenceChecker : ICrossServiceReferenceChecker
{
    private readonly GrpcClientFactory _grpcClientFactory;
    private readonly ILogger<GrpcCrossServiceReferenceChecker>? _logger;

    public GrpcCrossServiceReferenceChecker(
        GrpcClientFactory grpcClientFactory,
        ILogger<GrpcCrossServiceReferenceChecker>? logger = null)
    {
        _grpcClientFactory = grpcClientFactory;
        _logger = logger;
    }

    public static string NormalizeServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return string.Empty;
        }

        var normalized = serviceName.Trim();
        if (normalized.EndsWith("-service", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"-service".Length];
        }
        else if (normalized.EndsWith("_service", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"_service".Length];
        }
        else if (normalized.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
                 normalized.Length > "Service".Length)
        {
            normalized = normalized[..^"Service".Length];
        }

        normalized = normalized.Trim('-', '_', ' ').ToLowerInvariant();
        if (normalized == "api" || normalized.Contains("gateway", StringComparison.Ordinal))
        {
            return "gateway";
        }

        return normalized;
    }

    public static string GetClientName(string serviceName)
    {
        return $"icot-refcheck:{NormalizeServiceName(serviceName)}";
    }

    public async Task<IReadOnlyList<string>> CheckServicesAsync(
        IEnumerable<string> downstreamServices,
        string targetService,
        string targetTable,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetService);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (downstreamServices == null)
        {
            return Array.Empty<string>();
        }

        var servicesToCheck = downstreamServices
            .Select(NormalizeServiceName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (servicesToCheck.Length == 0)
        {
            return Array.Empty<string>();
        }

        var normalizedTargetService = NormalizeServiceName(targetService);
        var cleanTable = targetTable.Trim();
        var cleanId = targetId.Trim();
        var timeout = TimeSpan.FromSeconds(PlatformApiExtensions.GetIntEnv("GRPC_REFCHECK_TIMEOUT_SECONDS", 10, 1, 60));

        var tasks = servicesToCheck.Select(async serviceName =>
        {
            var client = _grpcClientFactory.CreateClient<ReferenceCheckGrpcService.ReferenceCheckGrpcServiceClient>(
                GetClientName(serviceName));

            var request = new CheckReferencesGrpcRequest
            {
                TargetService = normalizedTargetService,
                TargetTable = cleanTable,
                TargetId = cleanId
            };

            _logger?.LogDebug(
                "[ReferenceCheck] Checking downstream service {DownstreamService} for {TargetService}.{TargetTable} ({TargetId}).",
                serviceName, normalizedTargetService, cleanTable, cleanId);

            var response = await client.CheckReferencesAsync(
                request,
                deadline: DateTime.UtcNow.Add(timeout),
                cancellationToken: cancellationToken);

            return response.DependentTables;
        });

        var responses = await Task.WhenAll(tasks);

        return responses
            .SelectMany(tables => tables)
            .Where(table => !string.IsNullOrWhiteSpace(table))
            .Select(table => table.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
