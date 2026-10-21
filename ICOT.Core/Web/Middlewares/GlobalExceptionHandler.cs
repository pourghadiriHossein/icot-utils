using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HPK.Core.Utils.Responses;
using HPK.Core.Extensions;
using HPK.Core.Messaging.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HPK.Core.Middlewares;

/// <summary>
/// A global exception handler that catches unhandled exceptions and formats them into the standard <see cref="ApiResponse{T}"/> JSON format.
/// Logs the exception using standard .NET ILogger (which binds to Serilog).
/// Adheres to SRP by delegating mapping to <see cref="IExceptionResponseMapper"/> and queue publishing to <see cref="IPlatformErrorPublisher"/>.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;
    private readonly IExceptionResponseMapper _responseMapper;
    private readonly IPlatformErrorPublisher _errorPublisher;

    [ActivatorUtilitiesConstructor]
    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment env,
        IExceptionResponseMapper? responseMapper = null,
        IPlatformErrorPublisher? errorPublisher = null)
    {
        _logger = logger;
        _env = env;
        _responseMapper = responseMapper ?? new DefaultExceptionResponseMapper();
        _errorPublisher = errorPublisher ?? new MassTransitErrorPublisher();
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // 1. Log the error (Serilog will pick this up)
        _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

        var serviceName = PlatformApiExtensions.CurrentServiceName ?? "unknown";

        // If it's a gRPC request, let gRPC handle its own trailers and status codes
        if (httpContext.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = PublishErrorSafelyAsync(httpContext, serviceName, exception, cancellationToken);
            return false;
        }

        // 2. Prepare the standardized response via IExceptionResponseMapper (SRP & OCP)
        var mapping = _responseMapper.Map(exception, _env.IsDevelopment());
        var apiResponse = ApiResponse.Error(mapping.StatusCode, mapping.Message, mapping.Details);

        // 3. Write JSON response
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.StatusCode = mapping.StatusCode;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await httpContext.Response.WriteAsJsonAsync(apiResponse, jsonOptions, cancellationToken);

        // 4. Publish log:error event to ACTION_SERVICE_QUEUE (fire-and-forget safe background task)
        _ = PublishErrorSafelyAsync(httpContext, serviceName, exception, cancellationToken);

        // Return true to indicate the exception has been handled
        return true;
    }

    private async Task PublishErrorSafelyAsync(HttpContext httpContext, string serviceName, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            // Prevent recursion if action-service error-logs endpoint itself fails
            if (serviceName == "action" && httpContext.Request.Path.Value?.Contains("error-logs") == true)
                return;

            var meta = new Dictionary<string, object?>
            {
                ["source"] = "http",
                ["exception_type"] = exception.GetType().FullName,
                ["stack_trace"] = exception.StackTrace,
                ["inner_exception"] = exception.InnerException?.Message,
                ["path"] = httpContext.Request.Path.Value,
                ["method"] = httpContext.Request.Method,
                ["query"] = httpContext.Request.QueryString.Value,
                ["request_id"] = httpContext.Request.Headers["x-request"].ToString() is { Length: > 0 } req ? req : null,
                ["workspace_id"] = httpContext.Request.Headers["x-workspace"].ToString() is { Length: > 0 } ws ? ws : null,
                ["account_id"] = httpContext.Request.Headers["x-account"].ToString() is { Length: > 0 } acc ? acc : null,
                ["client_ip"] = httpContext.Request.Headers["x-forwarded-ip"].ToString() is { Length: > 0 } ip ? ip : httpContext.Connection.RemoteIpAddress?.ToString()
            };

            // Delegate error event publishing to IPlatformErrorPublisher (SRP & DIP)
            await _errorPublisher.PublishErrorAsync(serviceName, exception, meta, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GlobalExceptionHandler] Failed to publish log:error event.");
        }
    }
}
