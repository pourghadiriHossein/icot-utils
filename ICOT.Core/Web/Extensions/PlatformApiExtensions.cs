using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;
using DotNetEnv;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using System.Net.Http;

namespace ICOT.Core.Extensions;

public static class PlatformApiExtensions
{
    public static string CurrentServiceName { get; internal set; } = "unknown";

    public static WebApplicationBuilder ConfigurePlatformHost(this WebApplicationBuilder builder, string serviceName)
    {
        CurrentServiceName = serviceName.Replace("Service", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant().Trim('_');
        if (CurrentServiceName.Contains("gateway") || CurrentServiceName.Contains("api"))
        {
            CurrentServiceName = "gateway";
        }

        LoadEnvFile();

        var mode = Environment.GetEnvironmentVariable("MODE") ?? Env.GetString("MODE") ?? "development";
        bool isProduction = string.Equals(mode.Trim(), "production", StringComparison.OrdinalIgnoreCase);

        var aspnetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrEmpty(aspnetEnv))
        {
            builder.Environment.EnvironmentName = isProduction ? Environments.Production : Environments.Development;
        }
        else if (string.Equals(aspnetEnv, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            isProduction = true;
        }

        var cleanName = serviceName.Replace("Service", "", StringComparison.OrdinalIgnoreCase).Trim('_');
        var upperName = cleanName.ToUpper();
        if (upperName.Contains("GATEWAY") || upperName.Contains("API"))
        {
            upperName = "API_GATEWAY";
        }

        var urlKeys = new[]
        {
            $"{upperName}_SERVICE_URL",
            $"{upperName}_URL",
            $"POSTGRES_{upperName}_SERVICE_URL",
            $"POSTGRES_{upperName}_URL"
        };

        string? rawUrl = null;
        foreach (var key in urlKeys)
        {
            var val = Environment.GetEnvironmentVariable(key) ?? Env.GetString(key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                rawUrl = val.Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            uri = new Uri("http://localhost:5000");
        }

        var port = uri.Port > 0 ? uri.Port : 80;
        string bindingUrl = $"{uri.Scheme}://0.0.0.0:{port}";

        builder.WebHost.UseUrls(bindingUrl);
        builder.Configuration["ASPNETCORE_URLS"] = bindingUrl;
        builder.Configuration["ASPNETCORE_HTTP_PORTS"] = port.ToString();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions =>
            {
                // Gateway accepts HTTP/1.1 and HTTP/2 for external traffic.
                // Backend microservices listen with HTTP/2 for gRPC and API Gateway proxying.
                listenOptions.Protocols = CurrentServiceName.Equals("gateway", StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2
                    : Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });

        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        Console.WriteLine($"🌐 [{serviceName.ToUpper()}] Configured to listen on {bindingUrl}");

        return builder;
    }

    private static void LoadEnvFile()
    {
        var rootDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(rootDir, ".env")) && Directory.GetParent(rootDir) != null)
        {
            rootDir = Directory.GetParent(rootDir)!.FullName;
        }
        var envPath = Path.Combine(rootDir, ".env");
        if (File.Exists(envPath))
        {
            Env.Load(envPath);
            Env.TraversePath().Load();
        }
    }

    public static WebApplication UsePlatformApiStandard(this WebApplication app, string serviceName)
    {
        var cleanServiceName = serviceName.Replace("Service", "", StringComparison.OrdinalIgnoreCase).ToLower().Trim('_');
        if (cleanServiceName.Contains("gateway") || cleanServiceName.Contains("api"))
        {
            cleanServiceName = "gateway";
        }

        // Add Global Exception Handler Middleware
        app.UseExceptionHandler();

        // Ensure forwarded headers from Reverse Proxies (like YARP/NGINX) are applied
        // This fixes OpenAPI/Scalar generating internal network URLs in the servers list
        var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | 
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | 
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            ForwardLimit = null // Allow multiple hops (e.g. Cloudflare -> Liara Nginx -> API Gateway)
        };
        // By default, ASP.NET Core only trusts proxies from 127.0.0.1. 
        // In Docker, the Gateway has a different internal IP. We must clear these to trust our Gateway.
        forwardedOptions.KnownIPNetworks.Clear();
        forwardedOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedOptions);

        var pathBase = $"/api/v1/{cleanServiceName}";
        app.UsePathBase(pathBase);

        app.UseRouting();

        // Enforce x-platform / x-workspace tenant boundaries for backend services only
        if (cleanServiceName != "gateway")
        {
            app.UseMiddleware<ICOT.Core.Middlewares.TenantValidationMiddleware>();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();

        app.MapScalarApiReference("docs", options =>
        {
            options.Title = $"{cleanServiceName.ToUpper()} Service API Documentation";
            options.OpenApiRoutePattern = "openapi/v1.json";
            options.Authentication = new ScalarAuthenticationOptions
            {
                PreferredSecuritySchemes = new[] { "Bearer", "x-workspace", "x-language", "x-account", "x-platform", "x-permission", "x-scopes", "x-authentication", "x-authorization", "x-page" }
            };
        });

            Console.WriteLine($"📖 [{cleanServiceName.ToUpper()}] Docs available at: {pathBase}/docs");
        }

        return app;
    }

    public static IServiceCollection AddPlatformApiStandard(this IServiceCollection services)
    {
        services.AddTransient<ICOT.Core.Middlewares.RequestIdDelegatingHandler>();
        services.AddHttpClient("").AddHttpMessageHandler<ICOT.Core.Middlewares.RequestIdDelegatingHandler>();

        // Core SOLID Abstractions
        services.AddSingleton<ICOT.Core.Utils.Services.IEncryptionService, ICOT.Core.Utils.Services.AesEncryptionService>();
        services.AddSingleton<ICOT.Core.Utils.Services.IUserContextProvider, ICOT.Core.Utils.Services.HttpContextUserContextProvider>();
        services.AddSingleton<ICOT.Core.Messaging.Services.IPlatformErrorPublisher>(sp =>
            new ICOT.Core.Messaging.Services.MassTransitErrorPublisher(sp, sp.GetService<Microsoft.Extensions.Logging.ILogger<ICOT.Core.Messaging.Services.MassTransitErrorPublisher>>()));
        services.AddSingleton<ICOT.Core.Middlewares.IExceptionResponseMapper, ICOT.Core.Middlewares.DefaultExceptionResponseMapper>();
        services.AddSingleton<ICOT.Core.Middlewares.ITenantPolicyValidator, ICOT.Core.Middlewares.DefaultTenantPolicyValidator>();
        services.AddSingleton<ICOT.Core.Behaviors.ILogPayloadSanitizer, ICOT.Core.Behaviors.DefaultLogPayloadSanitizer>();
        services.AddSingleton<ICOT.Core.Filters.IPaginationMetadataExtractor, ICOT.Core.Filters.DefaultPaginationMetadataExtractor>();

        services.AddScoped<ICOT.Core.Utils.Services.IInternalValidationService, ICOT.Core.Utils.Services.InternalValidationService>();
        services.AddScoped<ICOT.Core.Utils.Services.IWorkspaceValidationService>(sp => sp.GetRequiredService<ICOT.Core.Utils.Services.IInternalValidationService>());
        services.AddScoped<ICOT.Core.Utils.Services.IAccountValidationService>(sp => sp.GetRequiredService<ICOT.Core.Utils.Services.IInternalValidationService>());

        services.AddGrpc();
        // 1. Add Global Exception Handler
        services.AddExceptionHandler<ICOT.Core.Middlewares.GlobalExceptionHandler>();
        services.AddProblemDetails();

        // 2. Configure Controllers to use the ApiResponseFilter and force application/json
        services.AddControllers(options => 
        {
            options.Filters.Add<ICOT.Core.Filters.ApiResponseFilter>();
            options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesAttribute("application/json"));
        })
        .AddJsonOptions(options =>
        {
            // Accept both string names ("A", "CNAME") and integer values (0, 2) for enums
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        })
        .ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .Select(e => e.Value!.Errors.First().ErrorMessage)
                    .FirstOrDefault();

                var message = errors ?? "One or more validation errors occurred.";
                var response = ICOT.Core.Utils.Responses.ApiResponse.Error(422, message);
                return new Microsoft.AspNetCore.Mvc.ObjectResult(response)
                {
                    StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status422UnprocessableEntity
                };
            };
        });
        
        services.AddHttpContextAccessor();
        // 3. Register global MediatR behavior for logging
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ICOT.Core.Behaviors.LogAddBehavior<,>));
        services.AddOpenApi(options =>
        {
            options.AddOperationTransformer((operation, context, cancellationToken) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var summaryAttr = metadata.OfType<Microsoft.AspNetCore.Http.EndpointSummaryAttribute>().FirstOrDefault();
                if (summaryAttr != null && !string.IsNullOrWhiteSpace(summaryAttr.Summary))
                {
                    operation.Summary = summaryAttr.Summary;
                }
                var descAttr = metadata.OfType<Microsoft.AspNetCore.Http.EndpointDescriptionAttribute>().FirstOrDefault();
                if (descAttr != null && !string.IsNullOrWhiteSpace(descAttr.Description))
                {
                    operation.Description = descAttr.Description;
                }
                return Task.CompletedTask;
            });

            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                var request = context.ApplicationServices.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()?.HttpContext?.Request;
                if (request != null)
                {
                    // Fallback to Request.Scheme/Host if X-Forwarded headers are missing
                    var proto = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
                    var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
                    
                    document.Servers ??= [];
                    document.Servers.Clear();
                    document.Servers.Add(new() 
                    { 
                        Url = $"{proto}://{host}{request.PathBase}",
                        Description = "API Gateway"
                    });
                }

                // Add Authentication (Bearer) to OpenAPI Document
                document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
                {
                    Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Enter your JWT token to authenticate."
                };

                var platformHeaders = new[]
                {
                    "x-account", "x-language", "x-workspace", "x-platform",
                    "x-permission", "x-scopes", "x-authentication", "x-authorization", "x-page"
                };

                foreach (var header in platformHeaders)
                {
                    document.Components.SecuritySchemes[header] = new Microsoft.OpenApi.OpenApiSecurityScheme
                    {
                        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
                        In = Microsoft.OpenApi.ParameterLocation.Header,
                        Name = header,
                        Description = $"Global header for {header}"
                    };
                }

                // Transform any remaining multilingual dictionary schemas in components or paths to show en/fa
                var visited = new HashSet<Microsoft.OpenApi.IOpenApiSchema>();
                if (document.Components?.Schemas != null)
                {
                    foreach (var s in document.Components.Schemas.Values)
                    {
                        ConfigureMultilingualSchema(s, visited);
                    }
                }
                if (document.Paths != null)
                {
                    foreach (var pathItem in document.Paths.Values)
                    {
                        if (pathItem.Operations != null)
                        {
                            foreach (var op in pathItem.Operations.Values)
                            {
                                if (op.RequestBody?.Content != null)
                                {
                                    foreach (var media in op.RequestBody.Content.Values)
                                    {
                                        ConfigureMultilingualSchema(media.Schema, visited);
                                    }
                                }
                                if (op.Responses != null)
                                {
                                    foreach (var resp in op.Responses.Values)
                                    {
                                        if (resp.Content != null)
                                        {
                                            foreach (var media in resp.Content.Values)
                                            {
                                                ConfigureMultilingualSchema(media.Schema, visited);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return Task.CompletedTask;
            });

            // Transform Dictionary<string, string> schemas to show en/fa properties and example
            options.AddSchemaTransformer((schema, context, cancellationToken) =>
            {
                var type = context.JsonTypeInfo.Type;
                if (type == typeof(Dictionary<string, string>) ||
                    type == typeof(IDictionary<string, string>) ||
                    type == typeof(IReadOnlyDictionary<string, string>))
                {
                    schema.Type = Microsoft.OpenApi.JsonSchemaType.Object;
                    schema.Properties ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
                    if (!schema.Properties.ContainsKey("en"))
                    {
                        schema.Properties["en"] = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String };
                    }
                    if (!schema.Properties.ContainsKey("fa"))
                    {
                        schema.Properties["fa"] = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String };
                    }
                    schema.Example ??= new System.Text.Json.Nodes.JsonObject
                    {
                        ["en"] = "",
                        ["fa"] = ""
                    };
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }

    private static void ConfigureMultilingualSchema(Microsoft.OpenApi.IOpenApiSchema? schemaNode, HashSet<Microsoft.OpenApi.IOpenApiSchema> visited)
    {
        if (schemaNode is not Microsoft.OpenApi.OpenApiSchema schema || !visited.Add(schema))
        {
            return;
        }

        if (schema.AdditionalProperties is Microsoft.OpenApi.OpenApiSchema addProp && addProp.Type == Microsoft.OpenApi.JsonSchemaType.String)
        {
            schema.Type = Microsoft.OpenApi.JsonSchemaType.Object;
            schema.Properties ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
            if (!schema.Properties.ContainsKey("en"))
            {
                schema.Properties["en"] = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String };
            }
            if (!schema.Properties.ContainsKey("fa"))
            {
                schema.Properties["fa"] = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String };
            }
            schema.Example ??= new System.Text.Json.Nodes.JsonObject
            {
                ["en"] = "",
                ["fa"] = ""
            };
        }

        if (schema.Properties != null)
        {
            foreach (var prop in schema.Properties.Values)
            {
                ConfigureMultilingualSchema(prop, visited);
            }
        }

        if (schema.Items != null)
        {
            ConfigureMultilingualSchema(schema.Items, visited);
        }
    }

    public static IHttpClientBuilder AddPlatformGrpcClient<TClient>(this IServiceCollection services, string serviceName) where TClient : class
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        return services.AddGrpcClient<TClient>(options =>
        {
            var cleanName = serviceName.Replace("Service", "", StringComparison.OrdinalIgnoreCase).Trim('_');
            var upperName = cleanName.ToUpperInvariant();
            if (upperName.Contains("GATEWAY") || upperName.Contains("API"))
            {
                upperName = "API_GATEWAY";
            }

            var urlKeys = new[]
            {
                $"{upperName}_SERVICE_URL",
                $"{upperName}_URL",
                $"POSTGRES_{upperName}_SERVICE_URL",
                $"POSTGRES_{upperName}_URL"
            };

            string? rawUrl = null;
            foreach (var key in urlKeys)
            {
                var val = Environment.GetEnvironmentVariable(key) ?? DotNetEnv.Env.GetString(key);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    rawUrl = val.Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                uri = new Uri("http://localhost:5000");
            }

            options.Address = uri;
        })
        .SetHandlerLifetime(TimeSpan.FromHours(1))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionLifetime = TimeSpan.FromHours(1),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        });
    }
}
