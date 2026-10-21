using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace ICOT.Core.Middlewares;

public record ExceptionMappingResult(int StatusCode, string Message, object? Details);

/// <summary>
/// Abstraction for mapping runtime exceptions to HTTP status codes and API error messages (SRP & OCP).
/// </summary>
public interface IExceptionResponseMapper
{
    ExceptionMappingResult Map(Exception exception, bool isDevelopment);
}

/// <summary>
/// Default implementation of <see cref="IExceptionResponseMapper"/> mapping standard .NET and HTTP exceptions.
/// </summary>
public class DefaultExceptionResponseMapper : IExceptionResponseMapper
{
    public ExceptionMappingResult Map(Exception exception, bool isDevelopment)
    {
        var statusCode = StatusCodes.Status500InternalServerError;
        var message = "An internal server error occurred.";

        switch (exception)
        {
            case ArgumentException:
                statusCode = StatusCodes.Status422UnprocessableEntity;
                message = exception.Message;
                break;
            case KeyNotFoundException:
                statusCode = StatusCodes.Status404NotFound;
                message = exception.Message;
                break;
            case InvalidOperationException:
                statusCode = StatusCodes.Status409Conflict;
                message = exception.Message;
                break;
            case UnauthorizedAccessException:
                statusCode = StatusCodes.Status401Unauthorized;
                message = exception.Message;
                break;
        }

        object? details = null;
        if (isDevelopment)
        {
            details = new
            {
                Type = exception.GetType().Name,
                exception.Message,
                exception.StackTrace
            };
            message = exception.Message;
        }

        return new ExceptionMappingResult(statusCode, message, details);
    }
}
