using Microsoft.EntityFrameworkCore;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Responsible for building dynamic LINQ filter expressions and translating JSON:API filter parameters.
/// </summary>
public static class JsonApiFilterBuilder
{
    public static IQueryable<T> ApplyFilter<T>(IQueryable<T> query, Dictionary<string, string>? filters) where T : class
    {
        if (filters == null || !filters.Any()) return query;

        var parameter = Expression.Parameter(typeof(T), "x");
        var andExpressions = new List<Expression>();

        var providerFullName = query.Provider.GetType().FullName ?? string.Empty;
        bool isRelational = providerFullName.Contains("EntityFrameworkCore")
                            && !providerFullName.Contains("InMemory");

        foreach (var filter in filters)
        {
            var rawKey = filter.Key.Trim();
            var rawValue = filter.Value?.Trim();
            if (string.IsNullOrEmpty(rawKey) || string.IsNullOrEmpty(rawValue)) continue;

            // Handle global OR across multiple fields: ?filter[$or]=age:>18|name:john|title.en:iran
            if (rawKey.Equals("$or", StringComparison.OrdinalIgnoreCase))
            {
                var orConditions = rawValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
                Expression? combinedOr = null;

                foreach (var condition in orConditions)
                {
                    var parts = condition.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var fieldKey = parts[0].Trim();
                        var conditionVal = parts[1].Trim();

                        if (TryResolveProperty(typeof(T), fieldKey, out var propInfo, out var subKey) && propInfo != null)
                        {
                            var expr = BuildSingleConditionExpression(parameter, propInfo, conditionVal, subKey, isRelational);
                            if (expr != null)
                            {
                                combinedOr = combinedOr == null ? expr : Expression.OrElse(combinedOr, expr);
                            }
                        }
                    }
                }

                if (combinedOr != null)
                {
                    andExpressions.Add(combinedOr);
                }
                continue;
            }

            // Regular filter field (including dictionary fields and nested keys like filter[title.en]=iran or filter[title][en]=iran)
            if (TryResolveProperty(typeof(T), rawKey, out var propertyInfo, out var fieldSubKey) && propertyInfo != null)
            {
                var expr = BuildSingleConditionExpression(parameter, propertyInfo, rawValue, fieldSubKey, isRelational);
                if (expr != null)
                {
                    andExpressions.Add(expr);
                }
            }
        }

        if (andExpressions.Any())
        {
            Expression combinedAnd = andExpressions.Aggregate(Expression.AndAlso);
            var lambda = Expression.Lambda<Func<T, bool>>(combinedAnd, parameter);
            query = query.Where(lambda);
        }

        return query;
    }

    private static bool TryResolveProperty(Type entityType, string rawKey, out PropertyInfo? propertyInfo, out string? subKey)
    {
        var normalizedKey = rawKey.Trim().Replace("][", ".").Replace("[", ".").Replace("]", "");
        propertyInfo = null;
        subKey = null;

        if (normalizedKey.Contains('.'))
        {
            var dotIdx = normalizedKey.IndexOf('.');
            var candidateField = ToPascalCase(normalizedKey.Substring(0, dotIdx));
            var candidateProp = entityType.GetProperty(candidateField, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (candidateProp != null && typeof(IDictionary).IsAssignableFrom(candidateProp.PropertyType))
            {
                propertyInfo = candidateProp;
                subKey = normalizedKey.Substring(dotIdx + 1);
                return true;
            }

            var directField = ToPascalCase(normalizedKey);
            var directProp = entityType.GetProperty(directField, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (directProp != null)
            {
                propertyInfo = directProp;
                return true;
            }

            if (candidateProp != null)
            {
                propertyInfo = candidateProp;
                subKey = normalizedKey.Substring(dotIdx + 1);
                return true;
            }
        }
        else
        {
            var field = ToPascalCase(normalizedKey);
            propertyInfo = entityType.GetProperty(field, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (propertyInfo != null)
            {
                return true;
            }
        }

        return false;
    }

    private static Expression? BuildSingleConditionExpression(
        Expression parameter, 
        PropertyInfo propertyInfo, 
        string rawValue, 
        string? subKey, 
        bool isRelational)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;

        var propertyType = propertyInfo.PropertyType;

        // 1. Dictionary property (multilingual jsonb)
        if (typeof(IDictionary).IsAssignableFrom(propertyType))
        {
            return BuildDictionaryCondition(parameter, propertyInfo, rawValue, subKey, isRelational);
        }

        // 2. String or Value type with multiple OR terms: e.g. status=active|pending or age=>18|<10
        // (excluding in: and nin:)
        if (rawValue.Contains('|') && 
            !rawValue.StartsWith("in:", StringComparison.OrdinalIgnoreCase) && 
            !rawValue.StartsWith("nin:", StringComparison.OrdinalIgnoreCase))
        {
            var terms = rawValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
            Expression? orExpr = null;
            foreach (var term in terms)
            {
                var singleExpr = BuildSingleValueCondition(parameter, propertyInfo, term.Trim(), isRelational);
                if (singleExpr != null)
                {
                    orExpr = orExpr == null ? singleExpr : Expression.OrElse(orExpr, singleExpr);
                }
            }
            return orExpr;
        }

        return BuildSingleValueCondition(parameter, propertyInfo, rawValue, isRelational);
    }

    private static Expression? BuildDictionaryCondition(
        Expression parameter, 
        PropertyInfo propertyInfo, 
        string value, 
        string? subKey, 
        bool isRelational)
    {
        var terms = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (!terms.Any()) return null;

        var propertyAccess = Expression.Property(parameter, propertyInfo);

        if (isRelational)
        {
            var convertToObject = Expression.Convert(propertyAccess, typeof(object));
            var convertToString = Expression.Convert(convertToObject, typeof(string));
            var efFunctions = Expression.Property(null, typeof(Microsoft.EntityFrameworkCore.EF), nameof(Microsoft.EntityFrameworkCore.EF.Functions));

            var iLikeMethod = typeof(Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions).GetMethod(
                nameof(Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions.ILike),
                new[] { typeof(Microsoft.EntityFrameworkCore.DbFunctions), typeof(string), typeof(string) })
                ?? typeof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions).GetMethod(
                nameof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions.Like),
                new[] { typeof(Microsoft.EntityFrameworkCore.DbFunctions), typeof(string), typeof(string) });

            if (iLikeMethod == null) return null;

            Expression? combinedExpr = null;

            foreach (var term in terms)
            {
                var cleanTerm = term.Trim();
                bool isExact = cleanTerm.StartsWith("==") || cleanTerm.StartsWith("=");
                bool isNotEqual = cleanTerm.StartsWith("!=") || cleanTerm.StartsWith("!");

                string searchVal = isExact
                    ? (cleanTerm.StartsWith("==") ? cleanTerm.Substring(2) : cleanTerm.Substring(1)).Trim()
                    : (isNotEqual ? (cleanTerm.StartsWith("!=") ? cleanTerm.Substring(2) : cleanTerm.Substring(1)).Trim() : cleanTerm);

                string pattern;
                if (!string.IsNullOrEmpty(subKey))
                {
                    pattern = isExact ? $"%\"{subKey}\":%\"{searchVal}\"%" : $"%\"{subKey}\":%{searchVal}%";
                }
                else
                {
                    pattern = isExact ? $"%\"{searchVal}\"%" : $"%{searchVal}%";
                }

                var patternConstant = Expression.Constant(pattern, typeof(string));
                Expression call = Expression.Call(iLikeMethod, efFunctions, convertToString, patternConstant);

                if (isNotEqual)
                {
                    var isNull = Expression.Equal(propertyAccess, Expression.Constant(null, propertyInfo.PropertyType));
                    call = Expression.OrElse(isNull, Expression.Not(call));
                }

                combinedExpr = combinedExpr == null ? call : Expression.OrElse(combinedExpr, call);
            }

            return combinedExpr;
        }
        else
        {
            // In-memory fallback
            var checkMethod = typeof(JsonApiFilterBuilder).GetMethod(nameof(CheckDictionaryMatch), BindingFlags.Public | BindingFlags.Static)!;
            var convertProp = Expression.Convert(propertyAccess, typeof(object));
            return Expression.Call(checkMethod, convertProp, Expression.Constant(value), Expression.Constant(subKey, typeof(string)));
        }
    }

    public static bool CheckDictionaryMatch(object? dictObj, string rawValue, string? subKey)
    {
        if (dictObj is not IDictionary dict) return false;

        var terms = rawValue.Split('|', StringSplitOptions.RemoveEmptyEntries);

        foreach (var term in terms)
        {
            var cleanTerm = term.Trim();
            bool isExact = cleanTerm.StartsWith("==") || cleanTerm.StartsWith("=");
            bool isNotEqual = cleanTerm.StartsWith("!=") || cleanTerm.StartsWith("!");

            string searchVal = isExact
                ? (cleanTerm.StartsWith("==") ? cleanTerm.Substring(2) : cleanTerm.Substring(1)).Trim()
                : (isNotEqual ? (cleanTerm.StartsWith("!=") ? cleanTerm.Substring(2) : cleanTerm.Substring(1)).Trim() : cleanTerm);

            bool match = false;
            if (!string.IsNullOrEmpty(subKey))
            {
                if (dict.Contains(subKey))
                {
                    var val = dict[subKey]?.ToString();
                    if (val != null)
                    {
                        match = isExact
                            ? string.Equals(val, searchVal, StringComparison.OrdinalIgnoreCase)
                            : val.Contains(searchVal, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            else
            {
                foreach (DictionaryEntry entry in dict)
                {
                    var val = entry.Value?.ToString();
                    if (val == null) continue;
                    if (isExact && string.Equals(val, searchVal, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!isExact && val.Contains(searchVal, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
            }

            if (isNotEqual ? !match : match) return true;
        }

        return false;
    }

    private static Expression? BuildSingleValueCondition(
        Expression parameter, 
        PropertyInfo propertyInfo, 
        string value, 
        bool isRelational)
    {
        var propertyType = propertyInfo.PropertyType;
        bool isString = propertyType == typeof(string);
        var propertyAccess = Expression.Property(parameter, propertyInfo);

        // IN / NOT IN
        if (value.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
        {
            var inValues = value.Substring(3).Split(',', StringSplitOptions.RemoveEmptyEntries);
            return BuildListContainmentExpression(propertyAccess, propertyType, inValues, isNot: false);
        }

        if (value.StartsWith("nin:", StringComparison.OrdinalIgnoreCase))
        {
            var ninValues = value.Substring(4).Split(',', StringSplitOptions.RemoveEmptyEntries);
            return BuildListContainmentExpression(propertyAccess, propertyType, ninValues, isNot: true);
        }

        string op = "==";
        string cleanValue = value;

        if (value.StartsWith(">=")) { op = ">="; cleanValue = value.Substring(2); }
        else if (value.StartsWith("<=")) { op = "<="; cleanValue = value.Substring(2); }
        else if (value.StartsWith("!=")) { op = "!="; cleanValue = value.Substring(2); }
        else if (value.StartsWith("!>")) { op = "<="; cleanValue = value.Substring(2); }
        else if (value.StartsWith("!<")) { op = ">="; cleanValue = value.Substring(2); }
        else if (value.StartsWith(">")) { op = ">"; cleanValue = value.Substring(1); }
        else if (value.StartsWith("<")) { op = "<"; cleanValue = value.Substring(1); }
        else if (value.StartsWith("!")) { op = "!="; cleanValue = value.Substring(1); }
        else if (value.StartsWith("==")) { op = "=="; cleanValue = value.Substring(2); }
        else if (value.StartsWith("=")) { op = "=="; cleanValue = value.Substring(1); }

        cleanValue = cleanValue.Trim();

        // Handle null / !null
        if (cleanValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            bool isNullable = !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null;
            if (!isNullable)
            {
                return (op == "!=" || value.StartsWith("!")) ? Expression.Constant(true) : Expression.Constant(false);
            }

            return (op == "!=" || value.StartsWith("!")) 
                ? Expression.NotEqual(propertyAccess, Expression.Constant(null, propertyType))
                : Expression.Equal(propertyAccess, Expression.Constant(null, propertyType));
        }

        if (isString)
        {
            if (op == "==" && (value.StartsWith("==") || value.StartsWith("=")))
            {
                return Expression.Equal(propertyAccess, Expression.Constant(cleanValue, typeof(string)));
            }
            if (op == "!=" || value.StartsWith("!"))
            {
                var isNull = Expression.Equal(propertyAccess, Expression.Constant(null, typeof(string)));
                var notEq = Expression.NotEqual(propertyAccess, Expression.Constant(cleanValue, typeof(string)));
                return Expression.OrElse(isNull, notEq);
            }

            // Default string behavior is ILIKE / Contains
            if (isRelational)
            {
                var efFunctions = Expression.Property(null, typeof(Microsoft.EntityFrameworkCore.EF), nameof(Microsoft.EntityFrameworkCore.EF.Functions));
                var iLikeMethod = typeof(Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions).GetMethod(
                    nameof(Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions.ILike),
                    new[] { typeof(Microsoft.EntityFrameworkCore.DbFunctions), typeof(string), typeof(string) })
                    ?? typeof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions).GetMethod(
                    nameof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions.Like),
                    new[] { typeof(Microsoft.EntityFrameworkCore.DbFunctions), typeof(string), typeof(string) });

                if (iLikeMethod != null)
                {
                    return Expression.Call(iLikeMethod, efFunctions, propertyAccess, Expression.Constant($"%{cleanValue}%"));
                }
            }

            // In-memory or fallback string contains
            var notNull = Expression.NotEqual(propertyAccess, Expression.Constant(null, typeof(string)));
            var containsMethod = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string), typeof(StringComparison) })!;
            var containsCall = Expression.Call(propertyAccess, containsMethod, Expression.Constant(cleanValue), Expression.Constant(StringComparison.OrdinalIgnoreCase));
            return Expression.AndAlso(notNull, containsCall);
        }
        else
        {
            try
            {
                var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                var convertedValue = ConvertValue(cleanValue, underlyingType);
                if (convertedValue == null) return null;

                Expression constant;
                if (propertyType != underlyingType)
                {
                    constant = Expression.Convert(Expression.Constant(convertedValue, underlyingType), propertyType);
                }
                else
                {
                    constant = Expression.Constant(convertedValue, propertyType);
                }

                return op switch
                {
                    ">=" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                    "<=" => Expression.LessThanOrEqual(propertyAccess, constant),
                    ">" => Expression.GreaterThan(propertyAccess, constant),
                    "<" => Expression.LessThan(propertyAccess, constant),
                    "!=" => Expression.NotEqual(propertyAccess, constant),
                    _ => Expression.Equal(propertyAccess, constant)
                };
            }
            catch
            {
                return null;
            }
        }
    }

    private static Expression? BuildListContainmentExpression(
        Expression propertyAccess, 
        Type propertyType, 
        string[] rawValues, 
        bool isNot)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var listType = typeof(List<>).MakeGenericType(underlyingType);
        var list = (IList?)Activator.CreateInstance(listType);
        if (list == null) return null;

        foreach (var v in rawValues)
        {
            var converted = ConvertValue(v.Trim(), underlyingType);
            if (converted != null)
            {
                list.Add(converted);
            }
        }

        var containsMethod = listType.GetMethod("Contains", new[] { underlyingType });
        if (containsMethod == null) return null;

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var hasValue = Expression.Property(propertyAccess, "HasValue");
            var valueAccess = Expression.Property(propertyAccess, "Value");
            var containsCall = Expression.Call(Expression.Constant(list), containsMethod, valueAccess);

            if (isNot)
            {
                return Expression.OrElse(Expression.Not(hasValue), Expression.Not(containsCall));
            }
            else
            {
                return Expression.AndAlso(hasValue, containsCall);
            }
        }
        else
        {
            var containsCall = Expression.Call(Expression.Constant(list), containsMethod, propertyAccess);
            return isNot ? Expression.Not(containsCall) : containsCall;
        }
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        var words = input.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            }
        }
        return string.Join("", words);
    }

    private static object? ConvertValue(string value, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        
        if (underlyingType == typeof(Guid))
            return Guid.Parse(value);
            
        if (underlyingType.IsEnum)
            return Enum.Parse(underlyingType, value, true);

        if (underlyingType == typeof(DateTime))
        {
            var dt = Convert.ToDateTime(value);
            if (dt.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt.ToUniversalTime();
        }

        if (underlyingType == typeof(bool))
            return bool.Parse(value);

        return Convert.ChangeType(value, underlyingType);
    }
}
