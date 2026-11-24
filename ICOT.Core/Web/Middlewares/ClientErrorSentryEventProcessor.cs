using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace ICOT.Core.Middlewares;

/// <summary>
/// Filters out handled HTTP client errors (4xx) and client cancellations (499) from Sentry error tracking
/// so Sentry's <c>IExceptionHandlerFeature</c> integration does not emit false error alerts for expected client outcomes.
/// </summary>
public sealed class ClientErrorSentryEventProcessor : ISentryEventProcessor
{
    private readonly IExceptionResponseMapper _responseMapper;
    private readonly IHostEnvironment _env;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public ClientErrorSentryEventProcessor(
        IExceptionResponseMapper responseMapper,
        IHostEnvironment env,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _responseMapper = responseMapper ?? throw new ArgumentNullException(nameof(responseMapper));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public SentryEvent? Process(SentryEvent @event)
    {
        if (@event == null)
        {
            return null;
        }

        return ShouldDropEvent(@event, _responseMapper, _env.IsDevelopment(), _httpContextAccessor?.HttpContext)
            ? null
            : @event;
    }

    internal static bool ShouldDropEvent(
        SentryEvent? sentryEvent,
        IExceptionResponseMapper responseMapper,
        bool isDevelopment,
        HttpContext? httpContext)
    {
        if (sentryEvent?.Exception is not { } exception)
        {
            return false;
        }

        if (GlobalExceptionHandler.IsClientErrorHandled(httpContext, exception))
        {
            return true;
        }

        if (IsExplicitClientException(exception))
        {
            return true;
        }

        if (IsFromExceptionHandlerFeature(sentryEvent)
            && responseMapper.Map(exception, isDevelopment).StatusCode < StatusCodes.Status500InternalServerError)
        {
            return true;
        }

        return false;
    }

    internal static bool IsExplicitClientException(Exception exception)
        => exception is Exceptions.DependencyConflictException
            or Exceptions.ValidationException
            or FluentValidation.ValidationException
            or Exceptions.ForbiddenAccessException;

    private static bool IsFromExceptionHandlerFeature(SentryEvent sentryEvent)
        => sentryEvent.SentryExceptions?.Any(e =>
               string.Equals(e.Mechanism?.Type, "IExceptionHandlerFeature", StringComparison.OrdinalIgnoreCase)) == true;
}

/// <summary>
/// Registers <see cref="ClientErrorSentryEventProcessor"/> on <see cref="SentryAspNetCoreOptions"/>
/// using DI-resolved <see cref="IExceptionResponseMapper"/> and <see cref="IHttpContextAccessor"/>.
/// </summary>
internal sealed class ClientErrorSentryOptionsSetup : IConfigureOptions<SentryAspNetCoreOptions>
{
    private readonly ClientErrorSentryEventProcessor _processor;

    public ClientErrorSentryOptionsSetup(ClientErrorSentryEventProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public void Configure(SentryAspNetCoreOptions options)
    {
        options.AddEventProcessor(_processor);
    }
}
