using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ICOT.Core.Events;

namespace ICOT.Core.Messaging.Services;

/// <summary>
/// Abstraction for publishing platform queue execution events to message queues (DIP & SRP).
/// Decouples microservices from MassTransit transport and queue naming.
/// </summary>
public interface IPlatformQueuePublisher
{
    Task PublishQueueAsync(QueueAddEvent queueEvent, CancellationToken cancellationToken = default);
    Task PublishQueueAsync(QueueAddEventData queueData, int priority = 2, CancellationToken cancellationToken = default);
    Task PublishQueueAsync(
        Guid? workspaceId,
        Guid? accountId,
        string service,
        string model,
        object request,
        Dictionary<string, object>? meta = null,
        int priority = 2,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default MassTransit implementation of <see cref="IPlatformQueuePublisher"/> publishing to ACTION_SERVICE_QUEUE.
/// </summary>
public class MassTransitQueuePublisher : IPlatformQueuePublisher
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ISendEndpointProvider? _directProvider;
    private readonly ILogger<MassTransitQueuePublisher>? _logger;

    [ActivatorUtilitiesConstructor]
    public MassTransitQueuePublisher(IServiceProvider serviceProvider, ILogger<MassTransitQueuePublisher>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    internal MassTransitQueuePublisher(ISendEndpointProvider directProvider, ILogger<MassTransitQueuePublisher>? logger = null)
    {
        _directProvider = directProvider;
        _logger = logger;
    }

    public static MassTransitQueuePublisher FromSendEndpoint(ISendEndpointProvider directProvider, ILogger<MassTransitQueuePublisher>? logger = null)
        => new(directProvider, logger);

    public async Task PublishQueueAsync(QueueAddEvent queueEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            ISendEndpointProvider? provider = _directProvider;

            if (provider == null && _serviceProvider != null)
            {
                var bus = _serviceProvider.GetService<IBus>();
                if (bus != null)
                {
                    provider = bus;
                }
                else
                {
                    using var scope = _serviceProvider.CreateScope();
                    provider = scope.ServiceProvider.GetService<ISendEndpointProvider>();
                }
            }

            if (provider == null) return;

            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE")
                         ?? DotNetEnv.Env.GetString("ACTION_SERVICE_QUEUE")
                         ?? "icot_action_queue";

            var sendEndpoint = await provider.GetSendEndpoint(new Uri($"exchange:{queueName}"));
            await sendEndpoint.Send(queueEvent, ctx => ctx.SetPriority((byte)Math.Clamp(queueEvent.Priority, 0, 10)), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitQueuePublisher] Failed to publish queue:add event.");
        }
    }

    public Task PublishQueueAsync(QueueAddEventData queueData, int priority = 2, CancellationToken cancellationToken = default)
    {
        var queueEvent = new QueueAddEvent(queueData, priority);
        return PublishQueueAsync(queueEvent, cancellationToken);
    }

    public Task PublishQueueAsync(
        Guid? workspaceId,
        Guid? accountId,
        string service,
        string model,
        object request,
        Dictionary<string, object>? meta = null,
        int priority = 2,
        CancellationToken cancellationToken = default)
    {
        var data = new QueueAddEventData
        {
            WorkspaceId = workspaceId,
            AccountId = accountId,
            Service = service,
            Model = model,
            Request = request,
            Meta = meta
        };

        return PublishQueueAsync(data, priority, cancellationToken);
    }
}
