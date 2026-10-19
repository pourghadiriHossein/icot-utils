using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using HPK.Core.Events;
using HPK.Core.Extensions;

namespace HPK.Core.Messaging.Observers;

public class PlatformConsumeErrorObserver : IConsumeObserver
{
    private readonly ILogger<PlatformConsumeErrorObserver>? _logger;

    public PlatformConsumeErrorObserver(ILogger<PlatformConsumeErrorObserver>? logger = null)
    {
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
            var queueName = Environment.GetEnvironmentVariable("ACTION_SERVICE_QUEUE") ?? DotNetEnv.Env.GetString("ACTION_SERVICE_QUEUE") ?? "icot_action_queue";
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

            var sendEndpoint = await context.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await sendEndpoint.Send(errorEvent);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[PlatformConsumeErrorObserver] Failed to publish log:error for faulted consumer.");
        }
    }
}
