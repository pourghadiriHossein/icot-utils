using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;

namespace HPK.Core.JsonApi;

/// <summary>
/// Model binding class for JSON:API specification query parameters.
/// Automatically binds ?filter[field]=value & sort=-field & include=rel & page[number]=1
/// </summary>
public class JsonApiQueryOptions
{
    [FromQuery(Name = "filter")]
    public Dictionary<string, string> Filter { get; set; } = new();

    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "include")]
    public string? Include { get; set; }

    [FromQuery(Name = "select")]
    public string? Select { get; set; }

    [FromQuery(Name = "page")]
    public Dictionary<string, int> Page { get; set; } = new();

    public int GetPageNumber() => Page.TryGetValue("number", out var num) && num > 0 ? num : 1;
    
    public int GetPageSize() => Page.TryGetValue("size", out var size) && size > 0 ? size : 10;
}
