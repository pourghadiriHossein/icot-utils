using Microsoft.EntityFrameworkCore;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HPK.Core.JsonApi;

public static class JsonApiExtensions
{
    /// <summary>
    /// Applies JSON:API specification query options (Filter, Sort, Include, Pagination) to an IQueryable.
    /// </summary>
    public static async Task<PagedResult<T>> ApplyJsonApiAsync<T>(this IQueryable<T> query, JsonApiQueryOptions options) where T : class
    {
        // 1. Includes (?include=author,comments.author)
        if (!string.IsNullOrWhiteSpace(options.Include))
        {
            var includes = options.Include.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var include in includes)
            {
                // Convert snake_case or camelCase to PascalCase if needed, but EF Core handles exact casing best.
                // Assuming standard JSON API convention maps to navigation properties.
                var navProperty = include.Trim().Replace(".", "."); // Nested paths work directly in EF (e.g. Author.Comments)
                query = query.Include(navProperty);
            }
        }

        // 2. Filtering (?filter[name]=john&filter[status]=active&filter[$or]=slug:iran|title:iran|title.en:iran)
        if (options.Filter != null && options.Filter.Any())
        {
            var parameter = Expression.Parameter(typeof(T), "x");
            var andExpressions = new List<Expression>();

            var providerFullName = query.Provider.GetType().FullName ?? string.Empty;
            bool isRelational = providerFullName.Contains("EntityFrameworkCore")
                                && !providerFullName.Contains("InMemory");

            foreach (var filter in options.Filter)
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
        }

        // 3. Sorting (?sort=-created_at,title)
        if (!string.IsNullOrWhiteSpace(options.Sort))
        {
            var sortFields = options.Sort.Split(',', StringSplitOptions.RemoveEmptyEntries);
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
                    validOrderByStrings.Add(isDescending ? $"{pascalField} descending" : pascalField);
                }
            }
            
            if (validOrderByStrings.Any())
            {
                var sortExpression = string.Join(", ", validOrderByStrings);
                query = query.OrderBy(sortExpression);
            }
        }

        // Fallback sorting if none provided (required by EF Core for Skip/Take)
        bool hasSort = !string.IsNullOrWhiteSpace(options.Sort);
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

        // 4. Pagination
        var totalCount = await query.CountAsync();
        var pageNumber = options.GetPageNumber();
        var pageSize = options.GetPageSize();

        var pagedData = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<T>(pagedData, totalCount, pageNumber, pageSize);
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
            var checkMethod = typeof(JsonApiExtensions).GetMethod(nameof(CheckDictionaryMatch), BindingFlags.Public | BindingFlags.Static)!;
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

    /// <summary>
    /// Dynamically shapes the output data by selecting or excluding fields based on a comma-separated list.
    /// Supports -field for exclusion, nested properties, and envelope dictionaries.
    /// </summary>
    public static object? ShapeData(object? data, string selectQuery)
    {
        if (data == null || string.IsNullOrWhiteSpace(selectQuery)) return data;

        var selectFields = selectQuery
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (!selectFields.Any()) return data;

        var includedFields = new List<string>();
        var excludedFields = new List<string>();

        foreach (var field in selectFields)
        {
            if (field.StartsWith("-"))
                excludedFields.Add(field.Substring(1).Trim());
            else
                includedFields.Add(field.Trim());
        }

        return ShapeValue(data, selectQuery, includedFields, excludedFields);
    }

    private static object? ShapeValue(
        object? data, 
        string selectQuery, 
        List<string> includedFields, 
        List<string> excludedFields)
    {
        if (data == null) return null;

        // 1. If data is an IDictionary (e.g. envelope {"error_logs": [...]}, {"error_log": {...}}, or dynamic record)
        if (data is IDictionary dict)
        {
            // Check if dict is an envelope or a property dictionary.
            // An envelope's keys are resource/table names like "error_logs", "error_log", "users", etc.
            // A property dictionary's keys match the select fields directly.
            bool hasDirectPropertyMatch = false;
            foreach (var key in dict.Keys)
            {
                var keyStr = key?.ToString();
                if (keyStr != null && (MatchesKey(keyStr, includedFields) || MatchesKey(keyStr, excludedFields)))
                {
                    hasDirectPropertyMatch = true;
                    break;
                }
            }

            if (!hasDirectPropertyMatch)
            {
                // Envelope dictionary: shape its inner values
                var newDict = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                {
                    var k = entry.Key?.ToString() ?? string.Empty;
                    newDict[k] = ShapeValue(entry.Value, selectQuery, includedFields, excludedFields);
                }
                return newDict;
            }
            else
            {
                // Property dictionary representing an entity
                return ShapeDictionaryItem(dict, includedFields, excludedFields);
            }
        }

        // 2. If data is a collection (IEnumerable excluding string and IDictionary)
        if (data is IEnumerable collection && data is not string)
        {
            var list = new List<object?>();
            foreach (var item in collection)
            {
                if (item == null)
                {
                    list.Add(null);
                    continue;
                }
                list.Add(ShapeSingleObject(item, includedFields, excludedFields));
            }
            return list;
        }

        // 3. Single entity object / DTO
        return ShapeSingleObject(data, includedFields, excludedFields);
    }

    private static object ShapeSingleObject(
        object item, 
        List<string> includedFields, 
        List<string> excludedFields)
    {
        if (item is IDictionary dict)
        {
            return ShapeDictionaryItem(dict, includedFields, excludedFields);
        }

        var itemType = item.GetType();
        if (IsSimpleType(itemType)) return item;

        var properties = itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var finalProps = new List<PropertyInfo>();

        if (includedFields.Any())
        {
            foreach (var prop in properties)
            {
                if (includedFields.Any(f => MatchesPropertyOrPrefix(prop, f)))
                {
                    finalProps.Add(prop);
                }
            }
        }
        else
        {
            finalProps.AddRange(properties);
        }

        if (excludedFields.Any())
        {
            finalProps.RemoveAll(prop => excludedFields.Any(f => MatchesPropertyExact(prop, f)));
        }

        var resultDict = new Dictionary<string, object?>();

        foreach (var prop in finalProps)
        {
            var key = GetJsonPropertyName(prop);
            var val = prop.GetValue(item);

            // Nested shaping support for dot-notation (e.g. "meta.ip" or "-meta.trace")
            var nestedIncludes = includedFields
                .Where(f => MatchesPropertyPrefix(prop, f))
                .Select(f => f.Substring(f.IndexOf('.') + 1))
                .ToList();

            var nestedExcludes = excludedFields
                .Where(f => MatchesPropertyPrefix(prop, f))
                .Select(f => f.Substring(f.IndexOf('.') + 1))
                .ToList();

            if ((nestedIncludes.Any() || nestedExcludes.Any()) && val != null && !IsSimpleType(val.GetType()))
            {
                var nestedQuery = string.Join(",", nestedIncludes.Concat(nestedExcludes.Select(e => "-" + e)));
                resultDict[key] = ShapeData(val, nestedQuery);
            }
            else
            {
                resultDict[key] = val;
            }
        }

        return resultDict;
    }

    private static Dictionary<string, object?> ShapeDictionaryItem(
        IDictionary dict, 
        List<string> includedFields, 
        List<string> excludedFields)
    {
        var finalKeys = new List<string>();
        var allKeys = new List<string>();

        foreach (var k in dict.Keys)
        {
            if (k != null) allKeys.Add(k.ToString()!);
        }

        if (includedFields.Any())
        {
            foreach (var k in allKeys)
            {
                if (includedFields.Any(f => MatchesKeyOrPrefix(k, f)))
                {
                    finalKeys.Add(k);
                }
            }
        }
        else
        {
            finalKeys.AddRange(allKeys);
        }

        if (excludedFields.Any())
        {
            finalKeys.RemoveAll(k => excludedFields.Any(f => MatchesKeyExact(k, f)));
        }

        var resultDict = new Dictionary<string, object?>();

        foreach (var k in finalKeys)
        {
            var val = dict[k];
            var nestedIncludes = includedFields
                .Where(f => MatchesKeyPrefix(k, f))
                .Select(f => f.Substring(f.IndexOf('.') + 1))
                .ToList();

            var nestedExcludes = excludedFields
                .Where(f => MatchesKeyPrefix(k, f))
                .Select(f => f.Substring(f.IndexOf('.') + 1))
                .ToList();

            if ((nestedIncludes.Any() || nestedExcludes.Any()) && val != null && !IsSimpleType(val.GetType()))
            {
                var nestedQuery = string.Join(",", nestedIncludes.Concat(nestedExcludes.Select(e => "-" + e)));
                resultDict[k] = ShapeData(val, nestedQuery);
            }
            else
            {
                resultDict[k] = val;
            }
        }

        return resultDict;
    }

    private static bool MatchesPropertyExact(PropertyInfo prop, string field)
    {
        var cleanField = field.Trim();
        if (cleanField.Contains('.')) return false;

        if (string.Equals(prop.Name, cleanField, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(prop.Name, ToPascalCase(cleanField), StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(ToSnakeCase(prop.Name), cleanField, StringComparison.OrdinalIgnoreCase))
            return true;

        var jsonPropAttr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        if (jsonPropAttr != null && string.Equals(jsonPropAttr.Name, cleanField, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool MatchesPropertyOrPrefix(PropertyInfo prop, string field)
    {
        var cleanField = field.Trim();
        var rootField = cleanField.Contains('.') ? cleanField.Substring(0, cleanField.IndexOf('.')) : cleanField;
        return MatchesPropertyExact(prop, rootField);
    }

    private static bool MatchesPropertyPrefix(PropertyInfo prop, string field)
    {
        var cleanField = field.Trim();
        if (!cleanField.Contains('.')) return false;
        var rootField = cleanField.Substring(0, cleanField.IndexOf('.'));
        return MatchesPropertyExact(prop, rootField);
    }

    private static bool MatchesKeyExact(string key, string field)
    {
        var cleanField = field.Trim();
        if (cleanField.Contains('.')) return false;

        if (string.Equals(key, cleanField, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(ToSnakeCase(key), cleanField, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(ToPascalCase(key), ToPascalCase(cleanField), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool MatchesKeyOrPrefix(string key, string field)
    {
        var cleanField = field.Trim();
        var rootField = cleanField.Contains('.') ? cleanField.Substring(0, cleanField.IndexOf('.')) : cleanField;
        return MatchesKeyExact(key, rootField);
    }

    private static bool MatchesKeyPrefix(string key, string field)
    {
        var cleanField = field.Trim();
        if (!cleanField.Contains('.')) return false;
        var rootField = cleanField.Substring(0, cleanField.IndexOf('.'));
        return MatchesKeyExact(key, rootField);
    }

    private static bool MatchesKey(string key, List<string> fields)
    {
        return fields.Any(f => MatchesKeyOrPrefix(key, f));
    }

    private static string GetJsonPropertyName(PropertyInfo prop)
    {
        var jsonPropAttr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        if (jsonPropAttr != null && !string.IsNullOrWhiteSpace(jsonPropAttr.Name))
            return jsonPropAttr.Name;

        return ToCamelCase(prop.Name);
    }

    private static string ToCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length == 1) return input.ToLowerInvariant();
        return char.ToLowerInvariant(input[0]) + input.Substring(1);
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && input[i - 1] != '_' && input[i - 1] != '-')
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
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
}
