using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ICOT.Core.Utils.Responses;
using System.Threading.Tasks;

namespace ICOT.Core.Filters;

/// <summary>
/// Intercepts successful API responses and wraps them in a standardized <see cref="ApiResponse{T}"/> format.
/// Automatically formats the output as application/json.
/// </summary>
public class ApiResponseFilter : IAsyncResultFilter
{
    private readonly IPaginationMetadataExtractor _paginationExtractor;

    public ApiResponseFilter(IPaginationMetadataExtractor? paginationExtractor = null)
    {
        _paginationExtractor = paginationExtractor ?? DefaultPaginationMetadataExtractor.Instance;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        // Enforce application/json for all responses
        context.HttpContext.Response.ContentType = "application/json";

        if (context.Result is ObjectResult objectResult)
        {
            string? selectString = null;
            if (context.HttpContext.Request.Query.TryGetValue("select", out var selectValues))
            {
                selectString = selectValues.ToString();
            }

            // If the controller already returned an ApiResponse (e.g., custom error logic), don't double-wrap
            if (objectResult.Value != null && objectResult.Value.GetType().IsGenericType &&
                objectResult.Value.GetType().GetGenericTypeDefinition() == typeof(ApiResponse<>))
            {
                if (!string.IsNullOrWhiteSpace(selectString))
                {
                    var valueType = objectResult.Value.GetType();
                    var dataProp = valueType.GetProperty("Data");
                    if (dataProp != null)
                    {
                        var originalData = dataProp.GetValue(objectResult.Value);
                        if (originalData != null)
                        {
                            var shapedData = ICOT.Core.JsonApi.JsonApiExtensions.ShapeData(originalData, selectString);

                            var successProp = valueType.GetProperty("Success");
                            var codeProp = valueType.GetProperty("Code");
                            var messageProp = valueType.GetProperty("Message");
                            var metaProp = valueType.GetProperty("Meta");
                            var exceptionProp = valueType.GetProperty("Exception");

                            bool success = (bool)(successProp?.GetValue(objectResult.Value) ?? true);
                            int code = (int)(codeProp?.GetValue(objectResult.Value) ?? 200);
                            string? message = (string?)messageProp?.GetValue(objectResult.Value);
                            object? meta = metaProp?.GetValue(objectResult.Value);
                            object? exception = exceptionProp?.GetValue(objectResult.Value);

                            objectResult.Value = new ApiResponse<object>(success, code, message, shapedData, meta, exception);
                        }
                    }
                }

                await next();
                return;
            }

            if (objectResult.Value is ApiResponse nonGenericApiResponse)
            {
                if (!string.IsNullOrWhiteSpace(selectString) && nonGenericApiResponse.Data != null)
                {
                    nonGenericApiResponse.Data = ICOT.Core.JsonApi.JsonApiExtensions.ShapeData(nonGenericApiResponse.Data, selectString);
                }

                await next();
                return;
            }

            // Extract pagination metadata and payload data via IPaginationMetadataExtractor (SRP)
            var (payloadData, metaData) = _paginationExtractor.Extract(objectResult.Value);

            // Apply Dynamic Select Shaping if ?select is provided
            if (!string.IsNullOrWhiteSpace(selectString) && payloadData != null)
            {
                payloadData = ICOT.Core.JsonApi.JsonApiExtensions.ShapeData(payloadData, selectString);
            }

            var statusCode = objectResult.StatusCode ?? context.HttpContext.Response.StatusCode;
            if (statusCode == 0) statusCode = 200;
            
            bool isSuccess = statusCode >= 200 && statusCode < 300;
            string defaultMessage = isSuccess ? "Operation completed successfully" : "An error occurred";
            
            // Try to extract a better error message if this is a ProblemDetails object
            if (!isSuccess && payloadData is Microsoft.AspNetCore.Mvc.ProblemDetails problemDetails)
            {
                defaultMessage = problemDetails.Title ?? "Validation failed";
            }

            // Wrap in our standard generic response
            var responseType = typeof(ApiResponse<>).MakeGenericType(payloadData?.GetType() ?? typeof(object));
            var wrappedResponse = Activator.CreateInstance(responseType, new object?[] 
            {
                isSuccess,                             // success flag
                statusCode,                            // code
                defaultMessage,                        // message
                payloadData,                           // data
                metaData,                              // meta
                null                                   // exception
            });

            context.Result = new ObjectResult(wrappedResponse)
            {
                StatusCode = statusCode,
                ContentTypes = new Microsoft.AspNetCore.Mvc.Formatters.MediaTypeCollection { "application/json" }
            };
        }
        else if (context.Result is EmptyResult || (context.Result is StatusCodeResult statusCodeResult && statusCodeResult.StatusCode >= 200 && statusCodeResult.StatusCode < 300))
        {
            var code = context.Result is StatusCodeResult sc ? sc.StatusCode : 200;
            var emptyResponse = ApiResponse.Ok();
            emptyResponse.Code = code;
            
            context.Result = new ObjectResult(emptyResponse)
            {
                StatusCode = code,
                ContentTypes = new Microsoft.AspNetCore.Mvc.Formatters.MediaTypeCollection { "application/json" }
            };
        }

        await next();
    }
}
