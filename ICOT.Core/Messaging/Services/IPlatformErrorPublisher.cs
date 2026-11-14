using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ICOT.Core.Events;
using ICOT.Core.Extensions;

namespace ICOT.Core.Messaging.Services;

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

    internal MassTransitErrorPublisher(ISendEndpointProvider directProvider, ILogger<MassTransitErrorPublisher>? logger = null)
    {
        _directProvider = directProvider;
        _logger = logger;
    }

    internal MassTransitErrorPublisher()
    {
    }

    public static MassTransitErrorPublisher FromSendEndpoint(ISendEndpointProvider directProvider, ILogger<MassTransitErrorPublisher>? logger = null)
        => new(directProvider, logger);

    /// <summary>
    /// Checks whether centralized RabbitMQ error logging (<c>log:error</c>) is enabled via the <c>LOG_ERRORS_ENABLED</c> environment variable.
    /// Defaults to <c>false</c> when missing, empty, or set to any value other than <c>"true"</c>.
    /// </summary>
    public static bool IsErrorLoggingEnabled()
    {
        var rawValue = Environment.GetEnvironmentVariable("LOG_ERRORS_ENABLED")
                    ?? DotNetEnv.Env.GetString("LOG_ERRORS_ENABLED");

        return !string.IsNullOrWhiteSpace(rawValue) &&
               string.Equals(rawValue.Trim().Trim('"', '\''), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task PublishErrorAsync(string serviceName, Exception exception, Dictionary<string, object?> meta, CancellationToken cancellationToken = default)
    {
        if (!IsErrorLoggingEnabled())
            return;

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

            var sendEndpoint = await provider.GetSendEndpoint(new Uri($"exchange:{queueName}"));
            await sendEndpoint.Send(errorEvent, ctx => ctx.SetPriority((byte)Math.Clamp(errorEvent.Priority, 0, 10)), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MassTransitErrorPublisher] Failed to publish log:error event.");
        }
    }
}
