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
/// Abstraction for publishing platform notification events to message queues (DIP & SRP).
/// Decouples microservices from MassTransit transport and queue naming.
/// </summary>
public interface IPlatformNotificationPublisher
{
    Task PublishNotificationAsync(NotificationAddEvent notificationEvent, CancellationToken cancellationToken = default);
    Task PublishNotificationAsync(NotificationAddEventData notificationData, int priority = 1, CancellationToken cancellationToken = default);
    Task PublishNotificationAsync(
        Guid? workspaceId,
        Guid? accountId,
        string service,
        string table,
        string model,
        string type,
        string message,
        Dictionary<string, object>? meta = null,
        int priority = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default MassTransit implementation of <see cref="IPlatformNotificationPublisher"/> publishing to ACTION_SERVICE_QUEUE.
/// </summary>
public class MassTransitNotificationPublisher : IPlatformNotificationPublisher
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ISendEndpointProvider? _directProvider;
    private readonly ILogger<MassTransitNotificationPublisher>? _logger;

    [ActivatorUtilitiesConstructor]
    public MassTransitNotificationPublisher(IServiceProvider serviceProvider, ILogger<MassTransitNotificationPublisher>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    internal MassTransitNotificationPublisher(ISendEndpointProvider directProvider, ILogger<MassTransitNotificationPublisher>? logger = null)
    {
        _directProvider = directProvider;
        _logger = logger;
    }

    public static MassTransitNotificationPublisher FromSendEndpoint(ISendEndpointProvider directProvider, ILogger<MassTransitNotificationPublisher>? logger = null)
        => new(directProvider, logger);

    public async Task PublishNotificationAsync(NotificationAddEvent notificationEvent, CancellationToken cancellationToken = default)
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

            var sendEndpoint = await provider.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(notificationEvent, ctx => ctx.SetPriority((byte)Math.Clamp(notificationEvent.Priority, 0, 10)), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitNotificationPublisher] Failed to publish notification:add event.");
        }
    }

    public Task PublishNotificationAsync(NotificationAddEventData notificationData, int priority = 1, CancellationToken cancellationToken = default)
    {
        var notificationEvent = new NotificationAddEvent(notificationData, priority);
        return PublishNotificationAsync(notificationEvent, cancellationToken);
    }

    public Task PublishNotificationAsync(
        Guid? workspaceId,
        Guid? accountId,
        string service,
        string table,
        string model,
        string type,
        string message,
        Dictionary<string, object>? meta = null,
        int priority = 1,
        CancellationToken cancellationToken = default)
    {
        var data = new NotificationAddEventData
        {
            WorkspaceId = workspaceId,
            AccountId = accountId,
            Service = service,
            Table = table,
            Model = model,
            Type = type,
            Message = message,
            Meta = meta
        };

        return PublishNotificationAsync(data, priority, cancellationToken);
    }
}
