using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using MediatR;
using ValidationException = ICOT.Core.Exceptions.ValidationException;

namespace ICOT.Core.Behaviors;

/// <summary>
/// Global MediatR pipeline behavior that runs all registered FluentValidation validators for an incoming command/query.
/// Throws a structured <see cref="ValidationException"/> when validation fails, which is mapped to HTTP 422 by GlobalExceptionHandler.
/// </summary>
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f != null)
            .ToList();

        if (failures.Count != 0)
        {
            var errors = failures
                .GroupBy(e => ToCamelCase(e.PropertyName))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).Distinct().ToArray()
                );

            throw new ValidationException(errors);
        }

        return await next();
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
