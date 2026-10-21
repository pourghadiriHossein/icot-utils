using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Responsible for dynamically shaping and projecting data based on JSON:API ?select fields.
/// Supports inclusion, -exclusion, nested dot-notation, envelope dictionaries, and JsonPropertyName preservation.
/// </summary>
public static class JsonApiDataShaper
{
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

        var jsonPropAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
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
        var jsonPropAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
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
