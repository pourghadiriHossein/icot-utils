using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace ICOT.Core.Middlewares;

public record ExceptionMappingResult(int StatusCode, string Message, object? Details = null, Dictionary<string, string[]>? Errors = null);

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
        Dictionary<string, string[]>? errors = null;

        switch (exception)
        {
            case Exceptions.ValidationException valEx:
                statusCode = StatusCodes.Status422UnprocessableEntity;
                message = valEx.Message;
                errors = valEx.Errors.ToDictionary(k => k.Key, v => v.Value);
                break;
            case FluentValidation.ValidationException fluentEx:
                statusCode = StatusCodes.Status422UnprocessableEntity;
                message = "One or more validation errors occurred.";
                errors = fluentEx.Errors
                    .GroupBy(e => ToCamelCase(e.PropertyName))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).Distinct().ToArray()
                    );
                break;
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
        if (isDevelopment && errors == null)
        {
            details = new
            {
                Type = exception.GetType().Name,
                exception.Message,
                exception.StackTrace
            };
            message = exception.Message;
        }

        return new ExceptionMappingResult(statusCode, message, details, errors);
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        if (str.StartsWith("$.")) str = str.Substring(2);
        else if (str.StartsWith("$")) str = str.Substring(1);

        var parts = str.Split('.');
        return string.Join(".", parts.Select(p =>
        {
            var bracketIndex = p.IndexOf('[');
            if (bracketIndex > 0)
            {
                var prop = p.Substring(0, bracketIndex);
                var rest = p.Substring(bracketIndex);
                return ToCamelCaseSegment(prop) + rest;
            }
            return ToCamelCaseSegment(p);
        }));
    }

    private static string ToCamelCaseSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length == 1) return s.ToLowerInvariant();
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}
