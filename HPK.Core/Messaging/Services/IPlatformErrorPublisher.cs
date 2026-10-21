using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
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
/// Can be safely registered as a Singleton without capturing scoped ISendEndpointProvider.
/// </summary>
public class MassTransitErrorPublisher : IPlatformErrorPublisher
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ISendEndpointProvider? _directProvider;
    private readonly ILogger<MassTransitErrorPublisher>? _logger;

    [ActivatorUtilitiesConstructor]
    public MassTransitErrorPublisher(IServiceProvider serviceProvider, ILogger<MassTransitErrorPublisher>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public MassTransitErrorPublisher(ISendEndpointProvider directProvider, ILogger<MassTransitErrorPublisher>? logger = null)
    {
        _directProvider = directProvider;
        _logger = logger;
    }

    public MassTransitErrorPublisher()
    {
    }

    public async Task PublishErrorAsync(string serviceName, Exception exception, Dictionary<string, object?> meta, CancellationToken cancellationToken = default)
    {
        try
        {
            ISendEndpointProvider? provider = _directProvider;

            if (provider == null && _serviceProvider != null)
            {
                // In MassTransit, IBus is a Singleton that implements ISendEndpointProvider
                var bus = _serviceProvider.GetService<IBus>();
                if (bus != null)
                {
                    provider = bus;
                }
                else
                {
                    // Fall back to resolving scoped ISendEndpointProvider from an on-demand scope
                    using var scope = _serviceProvider.CreateScope();
                    provider = scope.ServiceProvider.GetService<ISendEndpointProvider>();
                }
            }

            if (provider == null) return;

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

            var sendEndpoint = await provider.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(errorEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitErrorPublisher] Failed to publish log:error event.");
        }
    }
}
