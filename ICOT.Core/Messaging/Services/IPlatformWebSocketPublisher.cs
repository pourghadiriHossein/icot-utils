using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ICOT.Core.Events;

namespace ICOT.Core.Messaging.Services;

/// <summary>
/// Abstraction for publishing platform WebSocket events to API Gateway message queue (DIP & SRP).
/// Decouples microservices from MassTransit transport and queue naming.
/// </summary>
public interface IPlatformWebSocketPublisher
{
    Task PublishWebSocketMessageAsync(WebSocketSendEvent wsEvent, CancellationToken cancellationToken = default);
    Task PublishWebSocketMessageAsync(WebSocketSendEventData wsData, int priority = 1, CancellationToken cancellationToken = default);
    Task PublishWebSocketMessageAsync(
        Guid workspaceId,
        string scope,
        object message,
        Guid? accountId = null,
        string? signature = null,
        int priority = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default MassTransit implementation of <see cref="IPlatformWebSocketPublisher"/> publishing to API_GATEWAY_QUEUE.
/// </summary>
public class MassTransitWebSocketPublisher : IPlatformWebSocketPublisher
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ISendEndpointProvider? _directProvider;
    private readonly ILogger<MassTransitWebSocketPublisher>? _logger;

    [ActivatorUtilitiesConstructor]
    public MassTransitWebSocketPublisher(IServiceProvider serviceProvider, ILogger<MassTransitWebSocketPublisher>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    internal MassTransitWebSocketPublisher(ISendEndpointProvider directProvider, ILogger<MassTransitWebSocketPublisher>? logger = null)
    {
        _directProvider = directProvider;
        _logger = logger;
    }

    public static MassTransitWebSocketPublisher FromSendEndpoint(ISendEndpointProvider directProvider, ILogger<MassTransitWebSocketPublisher>? logger = null)
        => new(directProvider, logger);

    public async Task PublishWebSocketMessageAsync(WebSocketSendEvent wsEvent, CancellationToken cancellationToken = default)
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

            var queueName = Environment.GetEnvironmentVariable("API_GATEWAY_QUEUE") 
                         ?? DotNetEnv.Env.GetString("API_GATEWAY_QUEUE") 
                         ?? "icot_gateway_queue";

            var sendEndpoint = await provider.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(wsEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitWebSocketPublisher] Failed to publish ws:send event.");
        }
    }

    public Task PublishWebSocketMessageAsync(WebSocketSendEventData wsData, int priority = 1, CancellationToken cancellationToken = default)
    {
        var wsEvent = new WebSocketSendEvent(wsData, priority);
        return PublishWebSocketMessageAsync(wsEvent, cancellationToken);
    }

    public Task PublishWebSocketMessageAsync(
        Guid workspaceId,
        string scope,
        object message,
        Guid? accountId = null,
        string? signature = null,
        int priority = 1,
        CancellationToken cancellationToken = default)
    {
        var data = new WebSocketSendEventData
        {
            WorkspaceId = workspaceId,
            Scope = scope,
            AccountId = accountId,
            Signature = signature,
            Message = message
        };

        return PublishWebSocketMessageAsync(data, priority, cancellationToken);
    }
}
