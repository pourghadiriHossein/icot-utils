using System;
using System.Reflection;

namespace HPK.Core.Filters;

public record PaginationExtractionResult(object? PayloadData, object? MetaData);

/// <summary>
/// Abstraction for extracting pagination metadata from action results (SRP).
/// </summary>
public interface IPaginationMetadataExtractor
{
    PaginationExtractionResult Extract(object? resultValue);
}

/// <summary>
/// Default implementation of <see cref="IPaginationMetadataExtractor"/> inspecting PagedResult/Pagination types.
/// </summary>
public class DefaultPaginationMetadataExtractor : IPaginationMetadataExtractor
{
    public static DefaultPaginationMetadataExtractor Instance { get; } = new();

    public PaginationExtractionResult Extract(object? resultValue)
    {
        if (resultValue == null)
            return new PaginationExtractionResult(null, null);

        var valueType = resultValue.GetType();
        if (valueType.IsGenericType && (valueType.GetGenericTypeDefinition().Name.Contains("Paged") || valueType.GetGenericTypeDefinition().Name.Contains("Pagination")))
        {
            var countProp = valueType.GetProperty("TotalCount") ?? valueType.GetProperty("Count");
            var dataProp = valueType.GetProperty("Data") ?? valueType.GetProperty("Items");

            if (countProp != null && dataProp != null)
            {
                var totalCount = countProp.GetValue(resultValue);
                var currentPageProp = valueType.GetProperty("CurrentPage");
                var lastPageProp = valueType.GetProperty("LastPage");
                var perPageProp = valueType.GetProperty("PerPage");

                object? currentPage = currentPageProp?.GetValue(resultValue);
                object? lastPage = lastPageProp?.GetValue(resultValue);
                object? perPage = perPageProp?.GetValue(resultValue);

                var payloadData = dataProp.GetValue(resultValue);
                object metaData = currentPage != null
                    ? new { totalCount, currentPage, lastPage, perPage }
                    : new { totalCount };

                return new PaginationExtractionResult(payloadData, metaData);
            }
        }

        return new PaginationExtractionResult(resultValue, null);
    }
}
