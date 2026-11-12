using System;
using System.Collections.Generic;

namespace ICOT.Core.Exceptions;

/// <summary>
/// Domain/Application level exception thrown when request or entity validation fails.
/// Intercepted by GlobalExceptionHandler and mapped to HTTP 422 Unprocessable Entity.
/// </summary>
public class ValidationException : Exception
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string message = "One or more validation errors occurred.")
        : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(IDictionary<string, string[]> errors, string message = "One or more validation errors occurred.")
        : base(message)
    {
        Errors = errors;
    }
}
