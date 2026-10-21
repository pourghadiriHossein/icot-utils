using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ICOT.Core.JsonApi;

/// <summary>
/// Provides high-level extension methods for applying JSON:API filtering, sorting, includes, pagination, and data shaping.
/// Delegates specialized responsibilities to JsonApiFilterBuilder, JsonApiSortBuilder, and JsonApiDataShaper.
/// </summary>
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
                var navProperty = include.Trim().Replace(".", ".");
                query = query.Include(navProperty);
            }
        }

        // 2. Filtering (?filter[name]=john&filter[status]=active&filter[$or]=slug:iran|title:iran|title.en:iran)
        query = JsonApiFilterBuilder.ApplyFilter(query, options.Filter);

        // 3. Sorting (?sort=-created_at,title)
        query = JsonApiSortBuilder.ApplySort(query, options.Sort);

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

    /// <summary>
    /// Dynamically shapes the output data by selecting or excluding fields based on a comma-separated list.
    /// Supports -field for exclusion, nested properties, and envelope dictionaries.
    /// </summary>
    public static object? ShapeData(this object? data, string selectQuery)
    {
        return JsonApiDataShaper.ShapeData(data, selectQuery);
    }

    /// <summary>
    /// Backward-compatibility delegate for in-memory dictionary matching used in expression trees.
    /// </summary>
    public static bool CheckDictionaryMatch(object? dictObj, string rawValue, string? subKey)
    {
        return JsonApiFilterBuilder.CheckDictionaryMatch(dictObj, rawValue, subKey);
    }
}
