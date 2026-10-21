using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ICOT.Core.Middlewares;

public class RequestIdDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RequestIdDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context != null && context.Request.Headers.TryGetValue("x-request", out var requestId))
        {
            if (!request.Headers.Contains("x-request"))
            {
                request.Headers.TryAddWithoutValidation("x-request", requestId.ToString());
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
