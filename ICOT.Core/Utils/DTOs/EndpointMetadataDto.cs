using System;
using System.Collections.Generic;

namespace HPK.Core.Utils.DTOs;

public class EndpointMetadataDto
{
    public string Method { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public List<string> Scopes { get; set; } = new();
    public bool NeedAuthentication { get; set; } = true;
    public bool NeedAuthorization { get; set; } = true;
}
