using Microsoft.EntityFrameworkCore;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
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

            // Handle global OR across multiple fields: ?filter[$or]=age:>18|name:john|title.en:iran|type.key:abcd
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

                        var expr = BuildExpressionFromPath(parameter, typeof(T), fieldKey, conditionVal, isRelational);
                        if (expr != null)
                        {
                            combinedOr = combinedOr == null ? expr : Expression.OrElse(combinedOr, expr);
                        }
                    }
                }

                if (combinedOr != null)
                {
                    andExpressions.Add(combinedOr);
                }
                continue;
            }

            // Regular filter field (including dictionary fields and nested relation keys like filter[type.key]=abcd or sub_filter[type.key]=abcd)
            var singleExpr = BuildExpressionFromPath(parameter, typeof(T), rawKey, rawValue, isRelational);
            if (singleExpr != null)
            {
                andExpressions.Add(singleExpr);
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

    private static Expression? BuildExpressionFromPath(
        Expression currentExpr, 
        Type currentType, 
        string path, 
        string rawValue, 
        bool isRelational)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rawValue)) return null;

        var normalizedKey = path.Trim().Replace("][", ".").Replace("[", ".").Replace("]", "");

        if (!normalizedKey.Contains('.'))
        {
            var directProp = currentType.GetProperty(ToPascalCase(normalizedKey), BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (directProp != null)
            {
                var access = Expression.Property(currentExpr, directProp);
                return BuildConditionForExpression(access, directProp.PropertyType, rawValue, null, isRelational);
            }
            return null;
        }

        var dotIdx = normalizedKey.IndexOf('.');
        var firstSegment = normalizedKey.Substring(0, dotIdx);
        var remainingKey = normalizedKey.Substring(dotIdx + 1);

        // 1. Direct dictionary property (e.g. title.en or description.fa)
        var dictProp = currentType.GetProperty(ToPascalCase(firstSegment), BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (dictProp != null && typeof(IDictionary).IsAssignableFrom(dictProp.PropertyType))
        {
            var access = Expression.Property(currentExpr, dictProp);
            return BuildDictionaryCondition(access, dictProp.PropertyType, rawValue, remainingKey, isRelational);
        }

        // 2. Navigation Property (Reference or Collection)
        if (TryResolveNavigationProperty(currentType, firstSegment, out var navProp, out var isCollection, out var targetEntityType) && navProp != null && targetEntityType != null)
        {
            var navAccess = Expression.Property(currentExpr, navProp);

            if (isCollection)
            {
                var subParam = Expression.Parameter(targetEntityType, "sub");
                var subCond = BuildExpressionFromPath(subParam, targetEntityType, remainingKey, rawValue, isRelational);
                if (subCond == null) return null;

                var lambda = Expression.Lambda(subCond, subParam);
                var anyMethod = typeof(Enumerable).GetMethods()
                    .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                    .MakeGenericMethod(targetEntityType);
                var anyCall = Expression.Call(anyMethod, navAccess, lambda);

                var collNotNull = Expression.NotEqual(navAccess, Expression.Constant(null, navProp.PropertyType));
                return Expression.AndAlso(collNotNull, anyCall);
            }
            else
            {
                var navCond = BuildExpressionFromPath(navAccess, targetEntityType, remainingKey, rawValue, isRelational);
                if (navCond == null) return null;

                var notNull = Expression.NotEqual(navAccess, Expression.Constant(null, navProp.PropertyType));
                return Expression.AndAlso(notNull, navCond);
            }
        }

        // 3. Fallback direct property with dot in normalized form or full match (e.g. PascalCase direct field)
        var fallbackProp = currentType.GetProperty(ToPascalCase(normalizedKey), BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (fallbackProp != null)
        {
            var access = Expression.Property(currentExpr, fallbackProp);
            return BuildConditionForExpression(access, fallbackProp.PropertyType, rawValue, null, isRelational);
        }

        return null;
    }

    private static bool TryResolveNavigationProperty(
        Type entityType, 
        string segment, 
        out PropertyInfo? navProp, 
        out bool isCollection, 
        out Type? targetEntityType)
    {
        navProp = null;
        isCollection = false;
        targetEntityType = null;

        var pascalSegment = ToPascalCase(segment);
        var prop = entityType.GetProperty(pascalSegment, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        // 1. If property is found directly on entity
        if (prop != null)
        {
            var pType = prop.PropertyType;
            if (!IsSimpleType(pType) && !typeof(IDictionary).IsAssignableFrom(pType))
            {
                if (typeof(IEnumerable).IsAssignableFrom(pType))
                {
                    var elemType = GetCollectionElementType(pType);
                    if (elemType != null && !IsSimpleType(elemType))
                    {
                        navProp = prop;
                        isCollection = true;
                        targetEntityType = elemType;
                        return true;
                    }
                }
                else
                {
                    navProp = prop;
                    isCollection = false;
                    targetEntityType = pType;
                    return true;
                }
            }

            // If prop is a Foreign Key (e.g. Guid? Type or Guid CabinType), check for navigation property pointing to it
            var fkNav = entityType.GetProperty(prop.Name + "Enum", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                     ?? entityType.GetProperty(prop.Name + "Item", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                     ?? entityType.GetProperty(prop.Name + "Navigation", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                     ?? entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                  .FirstOrDefault(p => string.Equals(p.GetCustomAttribute<ForeignKeyAttribute>()?.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

            if (fkNav != null && !IsSimpleType(fkNav.PropertyType) && !typeof(IDictionary).IsAssignableFrom(fkNav.PropertyType))
            {
                navProp = fkNav;
                isCollection = false;
                targetEntityType = fkNav.PropertyType;
                return true;
            }

            // Check conventional FK suffix trim (e.g. "PlaceId" -> "Place")
            if (prop.Name.Length > 2 && prop.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
            {
                var trimmedNav = entityType.GetProperty(prop.Name.Substring(0, prop.Name.Length - 2), BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (trimmedNav != null && !IsSimpleType(trimmedNav.PropertyType) && !typeof(IDictionary).IsAssignableFrom(trimmedNav.PropertyType))
                {
                    navProp = trimmedNav;
                    isCollection = false;
                    targetEntityType = trimmedNav.PropertyType;
                    return true;
                }
            }
        }

        // 2. If prop wasn't directly found, check conventions: e.g. "type" -> "TypeEnum"
        var conventionNav = entityType.GetProperty(pascalSegment + "Enum", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                         ?? entityType.GetProperty(pascalSegment + "Item", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                         ?? entityType.GetProperty(pascalSegment + "Navigation", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                         ?? entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                      .FirstOrDefault(p => string.Equals(p.GetCustomAttribute<ForeignKeyAttribute>()?.Name, pascalSegment, StringComparison.OrdinalIgnoreCase));

        if (conventionNav != null && !IsSimpleType(conventionNav.PropertyType) && !typeof(IDictionary).IsAssignableFrom(conventionNav.PropertyType))
        {
            navProp = conventionNav;
            isCollection = false;
            targetEntityType = conventionNav.PropertyType;
            return true;
        }

        return false;
    }

    private static Expression? BuildConditionForExpression(
        Expression propertyAccess, 
        Type propertyType, 
        string rawValue, 
        string? subKey, 
        bool isRelational)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;

        // 1. Dictionary property (multilingual jsonb)
        if (typeof(IDictionary).IsAssignableFrom(propertyType))
        {
            return BuildDictionaryCondition(propertyAccess, propertyType, rawValue, subKey, isRelational);
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
                var singleExpr = BuildSingleValueCondition(propertyAccess, propertyType, term.Trim(), isRelational);
                if (singleExpr != null)
                {
                    orExpr = orExpr == null ? singleExpr : Expression.OrElse(orExpr, singleExpr);
                }
            }
            return orExpr;
        }

        return BuildSingleValueCondition(propertyAccess, propertyType, rawValue, isRelational);
    }

    private static Expression? BuildDictionaryCondition(
        Expression propertyAccess, 
        Type propertyType, 
        string value, 
        string? subKey, 
        bool isRelational)
    {
        var terms = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (!terms.Any()) return null;

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
                    var isNull = Expression.Equal(propertyAccess, Expression.Constant(null, propertyType));
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
        Expression propertyAccess, 
        Type propertyType, 
        string value, 
        bool isRelational)
    {
        bool isString = propertyType == typeof(string);

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

    private static bool IsSimpleType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive 
            || underlying.IsEnum 
            || underlying == typeof(string) 
            || underlying == typeof(decimal) 
            || underlying == typeof(DateTime) 
            || underlying == typeof(DateTimeOffset) 
            || underlying == typeof(TimeSpan) 
            || underlying == typeof(Guid);
    }

    private static Type? GetCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType();

        if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return collectionType.GetGenericArguments()[0];

        var enumInterface = collectionType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumInterface?.GetGenericArguments()[0];
    }
}
