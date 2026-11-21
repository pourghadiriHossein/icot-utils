using System;
using System.Collections.Generic;
using System.Linq;
using ICOT.Core.Exceptions;

namespace ICOT.Core.Deletion;

/// <summary>
/// Shared helper for aggregating local and remote dependent table names and throwing <see cref="DependencyConflictException"/>.
/// </summary>
public static class DeletionDependencyGuard
{
    public static IReadOnlyList<string> MergeDependentTables(params IEnumerable<string>?[] tableGroups)
    {
        if (tableGroups == null || tableGroups.Length == 0)
        {
            return Array.Empty<string>();
        }

        return tableGroups
            .Where(group => group != null)
            .SelectMany(group => group!)
            .Where(table => !string.IsNullOrWhiteSpace(table))
            .Select(table => table.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void ThrowIfHasDependencies(params IEnumerable<string>?[] tableGroups)
    {
        var merged = MergeDependentTables(tableGroups);
        if (merged.Count > 0)
        {
            throw new DependencyConflictException(merged);
        }
    }
}
