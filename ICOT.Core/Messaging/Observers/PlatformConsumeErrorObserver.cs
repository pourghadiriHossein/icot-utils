using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using ICOT.Core.Events;
using ICOT.Core.Extensions;
using ICOT.Core.Messaging.Services;

namespace ICOT.Core.Messaging.Observers;

public class PlatformConsumeErrorObserver : IConsumeObserver
{
    private readonly IPlatformErrorPublisher? _errorPublisher;
    private readonly ILogger<PlatformConsumeErrorObserver>? _logger;

    public PlatformConsumeErrorObserver(IPlatformErrorPublisher? errorPublisher = null, ILogger<PlatformConsumeErrorObserver>? logger = null)
    {
        _errorPublisher = errorPublisher;
        _logger = logger;
    }

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    public async Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        // Prevent infinite loop if LogErrorEvent itself fails
        if (typeof(T) == typeof(LogErrorEvent) || context.Message is LogErrorEvent)
            return;

        try
        {
            var serviceName = PlatformApiExtensions.CurrentServiceName ?? "unknown";

            var meta = new Dictionary<string, object?>
            {
                ["source"] = "queue",
                ["message_type"] = typeof(T).FullName,
                ["message_id"] = context.MessageId?.ToString(),
                ["correlation_id"] = context.CorrelationId?.ToString(),
                ["input_address"] = context.ReceiveContext.InputAddress?.ToString(),
                ["destination_address"] = context.DestinationAddress?.ToString(),
                ["exception_type"] = exception.GetType().FullName,
                ["stack_trace"] = exception.StackTrace,
                ["inner_exception"] = exception.InnerException?.Message
            };

            var publisher = _errorPublisher ?? new MassTransitErrorPublisher(context);
            await publisher.PublishErrorAsync(serviceName, exception, meta, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[PlatformConsumeErrorObserver] Failed to publish log:error for faulted consumer.");
        }
    }
}
