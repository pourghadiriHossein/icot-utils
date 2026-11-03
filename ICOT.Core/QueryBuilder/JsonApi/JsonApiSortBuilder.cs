using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Reflection;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Responsible for building dynamic LINQ sort expressions and validating JSON:API sort parameters.
/// </summary>
public static class JsonApiSortBuilder
{
    public static IQueryable<T> ApplySort<T>(IQueryable<T> query, string? sortQuery) where T : class
    {
        // 1. Explicit sort (?sort=-createdAt,title)
        if (!string.IsNullOrWhiteSpace(sortQuery))
        {
            var sortFields = sortQuery.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var validOrderByStrings = new List<string>();

            foreach (var f in sortFields)
            {
                var field = f.Trim();
                bool isDescending = field.StartsWith("-");
                var cleanField = isDescending ? field.Substring(1) : field;
                var pascalField = ToPascalCase(cleanField);

                var propertyInfo = typeof(T).GetProperty(pascalField, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (propertyInfo != null)
                {
                    validOrderByStrings.Add(isDescending ? $"{propertyInfo.Name} descending" : propertyInfo.Name);
                }
            }
            
            if (validOrderByStrings.Any())
            {
                var sortExpression = string.Join(", ", validOrderByStrings);
                query = query.OrderBy(sortExpression);
            }
        }

        // 2. Fallback sorting if none provided (required by EF Core for Skip/Take)
        bool hasSort = !string.IsNullOrWhiteSpace(sortQuery);
        if (!hasSort && !query.Expression.ToString().Contains("OrderBy"))
        {
            var createdAtProp = typeof(T).GetProperty("CreatedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            var idProp = typeof(T).GetProperty("Id", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            
            if (createdAtProp != null) 
            {
                query = query.OrderBy("CreatedAt descending");
            }
            else if (idProp != null) 
            {
                query = query.OrderBy("Id descending");
            }
        }

        return query;
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length == 1) return input.ToUpperInvariant();
        return char.ToUpperInvariant(input[0]) + input.Substring(1);
    }
}
