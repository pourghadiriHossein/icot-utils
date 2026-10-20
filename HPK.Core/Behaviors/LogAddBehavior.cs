using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

    private static readonly JsonSerializerOptions _logJsonOptions = CreateLogJsonOptions();

    public LogAddBehavior(ISendEndpointProvider sendEndpointProvider, IHttpContextAccessor httpContextAccessor)
    {
        _sendEndpointProvider = sendEndpointProvider;
        _httpContextAccessor = httpContextAccessor;
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

            // Capture sanitized request and response for Meta
            var metaData = new Dictionary<string, object?>();
            metaData["request"] = SanitizeForLog(request);

            if (response != null)
            {
                metaData["response"] = SanitizeForLog(response);
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

            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE") ?? "icot_action_queue";
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

    private static object? SanitizeForLog(object? obj)
    {
        if (obj == null) return null;

        var type = obj.GetType();
        if (IsFileOrStreamType(type) ||
            type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>)))
        {
            return "[File/Stream/Queryable Omitted]";
        }

        try
        {
            return JsonSerializer.SerializeToElement(obj, _logJsonOptions);
        }
        catch
        {
            return "[Unserializable Object]";
        }
    }

    private static bool IsFileOrStreamType(Type type)
    {
        if (typeof(Stream).IsAssignableFrom(type) ||
            typeof(IFormFile).IsAssignableFrom(type) ||
            typeof(IFormFileCollection).IsAssignableFrom(type))
        {
            return true;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
        {
            var itemType = type.GetGenericArguments().FirstOrDefault();
            if (itemType != null && (typeof(IFormFile).IsAssignableFrom(itemType) || typeof(Stream).IsAssignableFrom(itemType)))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonSerializerOptions CreateLogJsonOptions()
    {
        return new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        foreach (var prop in typeInfo.Properties)
                        {
                            if (IsFileOrStreamType(prop.PropertyType))
                            {
                                prop.ShouldSerialize = (_, _) => false;
                            }
                        }
                    }
                }
            }
        };
    }
}
