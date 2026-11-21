using System;

namespace ICOT.Core.Exceptions;

/// <summary>
/// Exception thrown when an authenticated caller lacks a required endpoint scope (e.g., force-delete permission).
/// Intercepted by GlobalExceptionHandler and mapped to HTTP 403 Forbidden.
/// </summary>
public class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message = "Access forbidden.")
        : base(message)
    {
    }
}
