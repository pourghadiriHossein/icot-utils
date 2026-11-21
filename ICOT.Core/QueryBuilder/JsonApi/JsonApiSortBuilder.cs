using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Responsible for building dynamic LINQ sort expressions and validating JSON:API sort parameters.
/// </summary>
public static class JsonApiSortBuilder
{
    public static IQueryable<T> ApplySort<T>(IQueryable<T> query, string? sortQuery) where T : class
    {
        bool appliedExplicitSort = false;
        var idProp = typeof(T).GetProperty("Id", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        var hasValidIdProp = idProp != null &&
                             idProp.CanRead &&
                             idProp.GetIndexParameters().Length == 0 &&
                             !Attribute.IsDefined(idProp, typeof(NotMappedAttribute)) &&
                             (idProp.PropertyType.IsValueType || idProp.PropertyType == typeof(string));

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
                if (hasValidIdProp && !validOrderByStrings.Any(s =>
                    s.Equals(idProp!.Name, StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith(idProp.Name + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    validOrderByStrings.Add($"{idProp!.Name} descending");
                }

                var sortExpression = string.Join(", ", validOrderByStrings);
                query = query.OrderBy(sortExpression);
                appliedExplicitSort = true;
            }
        }

        // 2. Fallback sorting if none was applied or ordering was erased (required by EF Core for Skip/Take)
        if (!appliedExplicitSort && !HasOrdering(query.Expression))
        {
            var createdAtProp = typeof(T).GetProperty("CreatedAt", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            
            if (createdAtProp != null) 
            {
                query = hasValidIdProp
                    ? query.OrderBy($"{createdAtProp.Name} descending, {idProp!.Name} descending")
                    : query.OrderBy($"{createdAtProp.Name} descending");
            }
            else if (hasValidIdProp) 
            {
                query = query.OrderBy($"{idProp!.Name} descending");
            }
            else
            {
                var fallbackProp = typeof(T)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p =>
                        p.CanRead &&
                        p.GetIndexParameters().Length == 0 &&
                        !Attribute.IsDefined(p, typeof(NotMappedAttribute)) &&
                        (p.PropertyType.IsValueType || p.PropertyType == typeof(string)));

                if (fallbackProp != null)
                {
                    query = query.OrderBy(fallbackProp.Name);
                }
            }
        }

        return query;
    }

    private static bool HasOrdering(Expression expression)
    {
        while (expression is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            if (methodName is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                return true;
            }

            if (methodName == "Distinct")
            {
                return false;
            }

            if (methodCall.Arguments.Count > 0)
            {
                expression = methodCall.Arguments[0];
            }
            else
            {
                break;
            }
        }

        return false;
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length == 1) return input.ToUpperInvariant();
        return char.ToUpperInvariant(input[0]) + input.Substring(1);
    }
}
