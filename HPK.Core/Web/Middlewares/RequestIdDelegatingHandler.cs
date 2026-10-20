using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace HPK.Core.Middlewares;

public class RequestIdDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RequestIdDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (context != null)
        {
            var reqId = context.Request.Headers["x-request-id"].ToString() is { Length: > 0 } id1
                ? id1
                : (context.Request.Headers["x-request"].ToString() is { Length: > 0 } id2 ? id2 : null);

            if (!string.IsNullOrWhiteSpace(reqId))
            {
                if (!request.Headers.Contains("x-request-id"))
                {
                    request.Headers.TryAddWithoutValidation("x-request-id", reqId);
                }
                if (!request.Headers.Contains("x-request"))
                {
                    request.Headers.TryAddWithoutValidation("x-request", reqId);
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
