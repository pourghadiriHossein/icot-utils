using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HPK.Core.Utils.Responses;
using HPK.Core.Extensions;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HPK.Core.Middlewares;

/// <summary>
/// A global exception handler that catches unhandled exceptions and formats them into the standard <see cref="ApiResponse{T}"/> JSON format.
/// Logs the exception using standard .NET ILogger (which binds to Serilog).
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // 1. Log the error (Serilog will pick this up)
        _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

        // 2. Prepare the standardized response
        var statusCode = StatusCodes.Status500InternalServerError;
        var message = "An internal server error occurred.";

        if (exception is ArgumentException)
        {
            statusCode = StatusCodes.Status422UnprocessableEntity;
            message = exception.Message;
        }
        else if (exception is KeyNotFoundException)
        {
            statusCode = StatusCodes.Status404NotFound;
            message = exception.Message;
        }
        else if (exception is InvalidOperationException)
        {
            statusCode = StatusCodes.Status409Conflict;
            message = exception.Message;
        }
        else if (exception is UnauthorizedAccessException)
        {
            statusCode = StatusCodes.Status401Unauthorized;
            message = exception.Message;
        }

        object? exceptionDetails = null;

        if (_env.IsDevelopment())
        {
            // Only expose stack trace details in development
            exceptionDetails = new
            {
                Type = exception.GetType().Name,
                exception.Message,
                exception.StackTrace
            };
            message = exception.Message;
        }

        var apiResponse = ApiResponse.Error(statusCode, message, exceptionDetails);

        // 3. Write JSON response
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.StatusCode = statusCode;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await httpContext.Response.WriteAsJsonAsync(apiResponse, jsonOptions, cancellationToken);

        // 4. Publish log:error event to ACTION_SERVICE_QUEUE (fire-and-forget safe background task)
        _ = PublishErrorLogAsync(httpContext, exception);

        // Return true to indicate the exception has been handled
        return true;
    }

    private async Task PublishErrorLogAsync(HttpContext httpContext, Exception exception)
    {
        try
        {
            var sendEndpointProvider = httpContext.RequestServices.GetService(typeof(MassTransit.ISendEndpointProvider)) as MassTransit.ISendEndpointProvider;
            if (sendEndpointProvider == null) return;

            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE") ?? DotNetEnv.Env.GetString("ACTION_SERVICE_QUEUE") ?? "icot_action_queue";
            var serviceName = PlatformApiExtensions.CurrentServiceName ?? "unknown";

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
                ["request_id"] = httpContext.Request.Headers["x-request-id"].ToString() is { Length: > 0 } reqId ? reqId : (httpContext.Request.Headers["x-request"].ToString() is { Length: > 0 } req ? req : null),
                ["workspace_id"] = httpContext.Request.Headers["x-workspace"].ToString() is { Length: > 0 } ws ? ws : null,
                ["account_id"] = httpContext.Request.Headers["x-account"].ToString() is { Length: > 0 } acc ? acc : null,
                ["client_ip"] = httpContext.Request.Headers["x-forwarded-ip"].ToString() is { Length: > 0 } ip ? ip : httpContext.Connection.RemoteIpAddress?.ToString()
            };

            var errorEvent = new HPK.Core.Events.LogErrorEvent
            {
                Event = "log:error",
                Priority = 1,
                Data = new HPK.Core.Events.LogErrorEventData
                {
                    Service = serviceName,
                    Message = exception.Message,
                    Meta = meta!
                }
            };

            var sendEndpoint = await sendEndpointProvider.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(errorEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GlobalExceptionHandler] Failed to publish log:error event.");
        }
    }
}
