using System.Diagnostics;
using PortKiller.UpdateManagement.Contracts;

namespace PortKiller.UpdateAdmin.Api;

public static class ApiProblems
{
    public static IResult Create(HttpContext context, int status, string code, string detail)
        => Results.Problem(statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?>
            { ["code"] = code, ["correlationId"] = Activity.Current?.Id ?? context.TraceIdentifier });

    public static Task WriteAsync(HttpContext context, int status, string code, string detail)
        => Create(context, status, code, detail).ExecuteAsync(context);
}
