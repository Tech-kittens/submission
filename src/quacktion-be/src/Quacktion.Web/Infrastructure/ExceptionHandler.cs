using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Quacktion.App.Services;
namespace Quacktion.Web.Infrastructure;

public sealed class ExceptionHandler(IProblemDetailsService problems, ILogger<ExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch { AppException app => app.Status, MongoConnectionException or MongoExecutionTimeoutException or TimeoutException => 503, OperationCanceledException => 499, _ => 500 };
        var code = exception is AppException known ? known.Code : status == 503 ? "database_unavailable" : "unexpected_error";
        if (status >= 500) logger.LogError(exception, "Request failed {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = new ProblemDetails { Status = status, Title = code, Extensions = { ["traceId"] = context.TraceIdentifier } } });
        return true;
    }
}
