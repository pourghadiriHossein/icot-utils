using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Http;
using HPK.Core.Events;
using HPK.Core.Interfaces;
using HPK.Core.Utils.Enums;

namespace HPK.Core.Behaviors;

public class LogAddBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogPayloadSanitizer _sanitizer;

    public LogAddBehavior(
        ISendEndpointProvider sendEndpointProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogPayloadSanitizer? sanitizer = null)
    {
        _sendEndpointProvider = sendEndpointProvider;
        _httpContextAccessor = httpContextAccessor;
        _sanitizer = sanitizer ?? DefaultLogPayloadSanitizer.Instance;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Execute the handler first
        var response = await next();

        // If request implements ILoggableRequest, publish log event
        if (request is ILoggableRequest loggable)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            
            // Seed Logging Exemption: If there is no HTTP Context (e.g. running via CLI or Startup Seeding), DO NOT generate action logs
            if (httpContext == null)
            {
                return response;
            }
            
            Guid? GetHeaderGuid(string headerName)
            {
                if (httpContext?.Request.Headers.TryGetValue(headerName, out var val) == true && Guid.TryParse(val.ToString(), out var guid))
                    return guid;
                return null;
            }

            // Capture sanitized request and response for Meta via ILogPayloadSanitizer (SRP)
            var metaData = new Dictionary<string, object?>
            {
                ["request"] = _sanitizer.Sanitize(request)
            };

            if (response != null)
            {
                metaData["response"] = _sanitizer.Sanitize(response);
            }

            var logEvent = new LogAddEvent
            {
                Event = "log:add",
                Priority = 1,
                Data = new LogAddEventData
                {
                    RequestId = GetHeaderGuid("x-request"),
                    QueueId = GetHeaderGuid("x-queue"),
                    WorkspaceId = GetHeaderGuid("x-workspace"),
                    AccountId = GetHeaderGuid("x-account"),
                    TraceId = GetHeaderGuid("x-trace-id"),
                    Service = loggable.LogService.ToLowerString(),
                    Table = loggable.LogTable,
                    Model = loggable.LogModel.ToLowerInvariant(),
                    Type = loggable.LogType.ToLowerString(),
                    Meta = metaData!
                }
            };

            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE") 
                         ?? DotNetEnv.Env.GetString("ACTION_SERVICE_QUEUE") 
                         ?? "icot_action_queue";
            try
            {
                var sendEndpoint = await _sendEndpointProvider.GetSendEndpoint(new Uri($"queue:{queueName}"));
                await sendEndpoint.Send(logEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                // Action logging failure should never abort the client's successful business operation
                Console.Error.WriteLine($"[LogAddBehavior] Warning: Failed to send log event to {queueName}: {ex.Message}");
            }
        }

        return response;
    }
}
