using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api;

public sealed class AdminExceptionHandler(ILogger<AdminExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        if (exception is AdminFailure error)
            await ApiProblems.WriteAsync(context, error.Status, error.Code, error.Message);
        else if (exception is BadHttpRequestException bad)
            await ApiProblems.WriteAsync(context, bad.StatusCode, "invalid_request", "请求格式或大小无效。");
        else if (exception is DbUpdateConcurrencyException)
            await ApiProblems.WriteAsync(context, 409, "resource_changed", "数据已变化，请刷新后重试。");
        else if (exception is SqliteException { SqliteErrorCode: 5 or 6 }
            || exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } })
        {
            context.Response.Headers.RetryAfter = "1";
            await ApiProblems.WriteAsync(context, 503, "database_busy", "服务繁忙，请稍后重试。");
        }
        else
        {
            logger.LogError("Request failed; correlation {Correlation}; failure type {FailureType}", context.TraceIdentifier, exception.GetType().Name);
            await ApiProblems.WriteAsync(context, 500, "internal_error", "请求暂时无法完成，请稍后重试。");
        }
        return true;
    }
}
