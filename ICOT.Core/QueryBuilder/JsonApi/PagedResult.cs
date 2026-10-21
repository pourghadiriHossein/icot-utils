using System;
using System.Collections.Generic;

namespace HPK.Core.JsonApi;

/// <summary>
/// A standardized paginated result container that integrates seamlessly with ApiResponseFilter.
/// </summary>
public class PagedResult<T>
{
    public IEnumerable<T> Data { get; set; }
    public long TotalCount { get; set; }
    public int CurrentPage { get; set; }
    public int PerPage { get; set; }
    public int LastPage { get; set; }
    
    public PagedResult(IEnumerable<T> data, long totalCount, int currentPage = 1, int perPage = 10)
    {
        Data = data;
        TotalCount = totalCount;
        CurrentPage = currentPage;
        PerPage = perPage;
        LastPage = perPage > 0 ? (int)Math.Ceiling(totalCount / (double)perPage) : 1;
        if (LastPage < 1) LastPage = 1;
    }
}
