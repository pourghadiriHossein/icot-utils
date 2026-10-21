using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using HPK.Core.Events;
using HPK.Core.Extensions;

namespace HPK.Core.Messaging.Services;

/// <summary>
/// Abstraction for publishing platform error events to message queues (DIP & SRP).
/// Decouples error handlers and observers from MassTransit transport and queue naming.
/// </summary>
public interface IPlatformErrorPublisher
{
    Task PublishErrorAsync(string serviceName, Exception exception, Dictionary<string, object?> meta, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default MassTransit implementation of <see cref="IPlatformErrorPublisher"/> publishing to ACTION_SERVICE_QUEUE.
/// </summary>
public class MassTransitErrorPublisher : IPlatformErrorPublisher
{
    private readonly ISendEndpointProvider? _sendEndpointProvider;
    private readonly ILogger<MassTransitErrorPublisher>? _logger;

    public MassTransitErrorPublisher(ISendEndpointProvider? sendEndpointProvider = null, ILogger<MassTransitErrorPublisher>? logger = null)
    {
        _sendEndpointProvider = sendEndpointProvider;
        _logger = logger;
    }

    public async Task PublishErrorAsync(string serviceName, Exception exception, Dictionary<string, object?> meta, CancellationToken cancellationToken = default)
    {
        if (_sendEndpointProvider == null) return;

        try
        {
            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE") 
                         ?? DotNetEnv.Env.GetString("ACTION_SERVICE_QUEUE") 
                         ?? "icot_action_queue";

            var errorEvent = new LogErrorEvent
            {
                Event = "log:error",
                Priority = 1,
                Data = new LogErrorEventData
                {
                    Service = serviceName,
                    Message = exception.Message,
                    Meta = meta!
                }
            };

            var sendEndpoint = await _sendEndpointProvider.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(errorEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitErrorPublisher] Failed to publish log:error event.");
        }
    }
}
