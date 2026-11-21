using System;
using System.Collections.Generic;
using System.Linq;

namespace ICOT.Core.Exceptions;

/// <summary>
/// Exception thrown when deleting a record with force=false is blocked by active dependent records.
/// Intercepted by GlobalExceptionHandler and mapped to HTTP 409 Conflict with <c>data.dependentTables</c>.
/// </summary>
public class DependencyConflictException : Exception
{
    public const string DefaultMessage =
        "Cannot delete this record because it is referenced by active records in dependent tables. Pass force=true with the required force-delete permission to proceed.";

    public IReadOnlyList<string> DependentTables { get; }

    public DependencyConflictException(IEnumerable<string> dependentTables, string? message = null)
        : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage : message)
    {
        DependentTables = (dependentTables ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
