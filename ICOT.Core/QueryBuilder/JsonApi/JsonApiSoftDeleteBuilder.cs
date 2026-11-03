using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Responsible for applying soft-delete / trash filtering to IQueryable instances based on JsonApiQueryOptions.
/// Ensures entities with a DeletedAt property exclude soft-deleted records by default,
/// unless trash=true is requested (in which case only deleted records are returned)
/// or trash=all (where all records are returned).
/// </summary>
public static class JsonApiSoftDeleteBuilder
{
    private const string DeletedAtPropertyName = "DeletedAt";

    /// <summary>
    /// Applies soft-delete filtering to the query if entity T contains a nullable DeletedAt property.
    /// </summary>
    public static IQueryable<T> ApplySoftDelete<T>(IQueryable<T> query, JsonApiQueryOptions? options) where T : class
    {
        var prop = typeof(T).GetProperty(DeletedAtPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (prop == null)
        {
            return query;
        }

        // Verify that the property is nullable (e.g. DateTime? or DateTimeOffset?)
        bool isNullable = !prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) != null;
        if (!isNullable)
        {
            return query;
        }

        var trashMode = options?.Trash?.Trim();

        // 1. Only trash requested: ?trash=true or ?trash=1 (x.DeletedAt != null)
        if (string.Equals(trashMode, "true", StringComparison.OrdinalIgnoreCase) || trashMode == "1")
        {
            var parameter = Expression.Parameter(typeof(T), "x");
            var propertyAccess = Expression.Property(parameter, prop);
            var nullConstant = Expression.Constant(null, prop.PropertyType);
            var notNullExpr = Expression.NotEqual(propertyAccess, nullConstant);
            var lambda = Expression.Lambda<Func<T, bool>>(notNullExpr, parameter);
            return query.Where(lambda);
        }

        // 2. All records requested: ?trash=all, ?trash=with, ?trash=* (no DeletedAt filter)
        if (string.Equals(trashMode, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trashMode, "with", StringComparison.OrdinalIgnoreCase) ||
            trashMode == "*")
        {
            return query;
        }

        // 3. If caller explicitly passed a filter on deletedAt in options.Filter, do not override it
        if (options?.Filter != null && (
            options.Filter.ContainsKey("deletedAt") ||
            options.Filter.ContainsKey("DeletedAt")))
        {
            return query;
        }

        // 4. Default mode (?trash=false, ?trash=0, or omitted): return only active records (x.DeletedAt == null)
        {
            var parameter = Expression.Parameter(typeof(T), "x");
            var propertyAccess = Expression.Property(parameter, prop);
            var nullConstant = Expression.Constant(null, prop.PropertyType);
            var isNullExpr = Expression.Equal(propertyAccess, nullConstant);
            var lambda = Expression.Lambda<Func<T, bool>>(isNullExpr, parameter);
            return query.Where(lambda);
        }
    }
}
