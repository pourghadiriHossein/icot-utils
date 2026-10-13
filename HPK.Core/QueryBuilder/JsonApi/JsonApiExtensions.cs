using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Linq.Dynamic.Core;
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
            var includes = options.Include.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var include in includes)
            {
                // Convert snake_case or camelCase to PascalCase if needed, but EF Core handles exact casing best.
                // Assuming standard JSON API convention maps to navigation properties.
                var navProperty = include.Trim().Replace(".", "."); // Nested paths work directly in EF (e.g. Author.Comments)
                query = query.Include(navProperty);
            }
        }

        // 2. Filtering (?filter[name]=john&filter[status]=active)
        if (options.Filter != null && options.Filter.Any())
        {
            var andClauses = new System.Collections.Generic.List<string>();
            var parameters = new System.Collections.Generic.List<object>();

            foreach (var filter in options.Filter)
            {
                var rawKey = filter.Key.Trim();
                var rawValue = filter.Value?.Trim();
                if (string.IsNullOrEmpty(rawKey) || string.IsNullOrEmpty(rawValue)) continue;

                // Handle global OR across multiple fields: ?filter[$or]=age:>18|name:john
                if (rawKey.Equals("$or", System.StringComparison.OrdinalIgnoreCase))
                {
                    var orConditions = rawValue.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
                    var orClauses = new System.Collections.Generic.List<string>();
                    foreach (var condition in orConditions)
                    {
                        var parts = condition.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var orFieldName = ToPascalCase(parts[0].Trim());
                            var orPropInfo = typeof(T).GetProperty(orFieldName, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (orPropInfo != null)
                            {
                                var parsed = BuildCondition(orFieldName, parts[1].Trim(), orPropInfo.PropertyType, parameters);
                                if (!string.IsNullOrEmpty(parsed)) orClauses.Add(parsed);
                            }
                        }
                    }
                    if (orClauses.Any()) andClauses.Add($"({string.Join(" || ", orClauses)})");
                    continue;
                }

                // Normal AND field
                var field = ToPascalCase(rawKey);
                var propertyInfo = typeof(T).GetProperty(field, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (propertyInfo == null) continue;

                // Handle OR for the same field: ?filter[status]=active|pending (not starting with in:/nin:)
                if (rawValue.Contains("|") && !rawValue.StartsWith("in:", System.StringComparison.OrdinalIgnoreCase) && !rawValue.StartsWith("nin:", System.StringComparison.OrdinalIgnoreCase))
                {
                    var fieldOrClauses = new System.Collections.Generic.List<string>();
                    var values = rawValue.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
                    foreach (var v in values)
                    {
                        var parsed = BuildCondition(field, v.Trim(), propertyInfo.PropertyType, parameters);
                        if (!string.IsNullOrEmpty(parsed)) fieldOrClauses.Add(parsed);
                    }
                    if (fieldOrClauses.Any()) andClauses.Add($"({string.Join(" || ", fieldOrClauses)})");
                }
                else
                {
                    var parsed = BuildCondition(field, rawValue, propertyInfo.PropertyType, parameters);
                    if (!string.IsNullOrEmpty(parsed)) andClauses.Add(parsed);
                }
            }

            if (andClauses.Any())
            {
                var combinedWhere = string.Join(" && ", andClauses);
                query = query.Where(combinedWhere, parameters.ToArray());
            }
        }

        // 3. Sorting (?sort=-created_at,title)
        if (!string.IsNullOrWhiteSpace(options.Sort))
        {
            var sortFields = options.Sort.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            var validOrderByStrings = new System.Collections.Generic.List<string>();

            foreach (var f in sortFields)
            {
                var field = f.Trim();
                bool isDescending = field.StartsWith("-");
                var cleanField = isDescending ? field.Substring(1) : field;
                var pascalField = ToPascalCase(cleanField);

                var propertyInfo = typeof(T).GetProperty(pascalField, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
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
            var createdAtProp = typeof(T).GetProperty("CreatedAt", System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var idProp = typeof(T).GetProperty("Id", System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
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

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        var words = input.Split(new[] { '_', '-' }, System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            }
        }
        return string.Join("", words);
    }

    private static string BuildCondition(string field, string value, System.Type propertyType, System.Collections.Generic.List<object> parameters)
    {
        bool isString = propertyType == typeof(string);

        // IN / NOT IN
        if (value.StartsWith("in:", System.StringComparison.OrdinalIgnoreCase))
        {
            var inValues = value.Substring(3).Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            var typedList = ConvertList(inValues, propertyType);
            if (typedList != null)
            {
                parameters.Add(typedList);
                return $"@{parameters.Count - 1}.Contains({field})";
            }
            return null;
        }
        if (value.StartsWith("nin:", System.StringComparison.OrdinalIgnoreCase))
        {
            var ninValues = value.Substring(4).Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            var typedList = ConvertList(ninValues, propertyType);
            if (typedList != null)
            {
                parameters.Add(typedList);
                return $"!@{parameters.Count - 1}.Contains({field})";
            }
            return null;
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

        // Handle null / !null filtering for nullable and reference properties
        if (cleanValue.Equals("null", System.StringComparison.OrdinalIgnoreCase))
        {
            bool isNullable = !propertyType.IsValueType || System.Nullable.GetUnderlyingType(propertyType) != null;
            if (!isNullable)
            {
                return (op == "!=" || value.StartsWith("!")) ? null : "1 == 0";
            }

            return (op == "!=" || value.StartsWith("!")) ? $"{field} != null" : $"{field} == null";
        }

        if (isString)
        {
            if (op == "==" && (value.StartsWith("==") || value.StartsWith("="))) 
            {
                parameters.Add(cleanValue);
                return $"{field} == @{parameters.Count - 1}";
            }
            if (op == "!=" || value.StartsWith("!"))
            {
                parameters.Add(cleanValue);
                return $"{field} != @{parameters.Count - 1}";
            }
            
            // Default string behavior is Contains
            parameters.Add(cleanValue);
            return $"{field}.Contains(@{parameters.Count - 1})";
        }
        else
        {
            try
            {
                var convertedValue = ConvertValue(cleanValue, propertyType);
                parameters.Add(convertedValue);
                return $"{field} {op} @{parameters.Count - 1}";
            }
            catch
            {
                return null; 
            }
        }
    }

    private static object ConvertValue(string value, System.Type targetType)
    {
        var underlyingType = System.Nullable.GetUnderlyingType(targetType) ?? targetType;
        
        if (underlyingType == typeof(System.Guid))
            return System.Guid.Parse(value);
            
        if (underlyingType.IsEnum)
            return System.Enum.Parse(underlyingType, value, true);

        if (underlyingType == typeof(System.DateTime))
        {
            var dt = System.Convert.ToDateTime(value);
            if (dt.Kind == System.DateTimeKind.Unspecified)
                return System.DateTime.SpecifyKind(dt, System.DateTimeKind.Utc);
            return dt.ToUniversalTime();
        }

        return System.Convert.ChangeType(value, underlyingType);
    }

    private static System.Collections.IList ConvertList(string[] values, System.Type targetType)
    {
        try
        {
            var underlyingType = System.Nullable.GetUnderlyingType(targetType) ?? targetType;
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(underlyingType);
            var list = (System.Collections.IList)System.Activator.CreateInstance(listType);

            foreach (var v in values)
            {
                list.Add(ConvertValue(v.Trim(), underlyingType));
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dynamically shapes the output data by selecting or excluding fields based on a comma-separated list.
    /// Supports -field for exclusion.
    /// </summary>
    public static object ShapeData(object data, string selectQuery)
    {
        if (data == null || string.IsNullOrWhiteSpace(selectQuery)) return data;

        bool isCollection = data is System.Collections.IEnumerable && data.GetType() != typeof(string);
        
        var elementType = isCollection 
            ? (data.GetType().GetGenericArguments().FirstOrDefault() ?? data.GetType().GetElementType())
            : data.GetType();

        if (elementType == null) return data;

        var properties = elementType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Select(p => p.Name)
                                    .ToList();

        var selectFields = selectQuery.Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        var includedFields = new System.Collections.Generic.List<string>();
        var excludedFields = new System.Collections.Generic.List<string>();

        foreach (var field in selectFields)
        {
            if (field.StartsWith("-")) excludedFields.Add(ToPascalCase(field.Substring(1)));
            else includedFields.Add(ToPascalCase(field));
        }

        var finalFields = new System.Collections.Generic.List<string>();

        if (includedFields.Any())
        {
            finalFields = properties.Where(p => includedFields.Contains(p, System.StringComparer.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            finalFields = properties.ToList();
        }

        if (excludedFields.Any())
        {
            finalFields.RemoveAll(p => excludedFields.Contains(p, System.StringComparer.OrdinalIgnoreCase));
        }

        if (!finalFields.Any()) return data;

        // Use dictionaries for shaping to avoid Dynamic LINQ reserved keyword issues (like 'Parent')
        if (isCollection)
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            foreach (var item in (System.Collections.IEnumerable)data)
            {
                if (item == null) continue;
                var dict = new System.Collections.Generic.Dictionary<string, object>();
                foreach (var field in finalFields)
                {
                    var prop = elementType.GetProperty(field);
                    if (prop != null)
                        dict[field] = prop.GetValue(item);
                }
                list.Add(dict);
            }
            return list;
        }
        else
        {
            var dict = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var field in finalFields)
            {
                var prop = elementType.GetProperty(field);
                if (prop != null)
                    dict[field] = prop.GetValue(data);
            }
            return dict;
        }
    }
}
