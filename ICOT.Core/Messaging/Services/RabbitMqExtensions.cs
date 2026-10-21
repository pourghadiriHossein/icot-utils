using System;
using DotNetEnv;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace ICOT.Core.Utils.Services;

public static class RabbitMqExtensions
{
    public static IServiceCollection AddPlatformMassTransit(
        this IServiceCollection services, 
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        services.AddMassTransit(x =>
        {
            configure?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                var host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? Env.GetString("RABBITMQ_HOST") ?? "localhost";
                var portStr = Environment.GetEnvironmentVariable("RABBITMQ_PORT") ?? Env.GetString("RABBITMQ_PORT");
                var port = int.TryParse(portStr, out int p) ? p : 5672;
                var user = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? Env.GetString("RABBITMQ_USER") ?? "docker";
                var pass = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? Env.GetString("RABBITMQ_PASS") ?? "docker";

                cfg.Host(host, (ushort)port, "/", h =>
                {
                    h.Username(user);
                    h.Password(pass);
                });

                // Use Raw JSON serialization across all platform microservices
                cfg.UseRawJsonSerializer(RawSerializerOptions.AnyMessageType);

                // Connect automatic error logging observer for failed consumers
                cfg.ConnectConsumeObserver(new ICOT.Core.Messaging.Observers.PlatformConsumeErrorObserver());

                // Configure standard durability etc automatically.
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
