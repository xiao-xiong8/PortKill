using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Releases;

public sealed class JobEventStream(IServiceScopeFactory scopes, AdminOptions options, JobStreamConnections connections,
    IOptions<JsonOptions> json, IHostApplicationLifetime lifetime)
{
    public async Task WriteAsync(HttpContext context, string id, string? after, string? lastEventId = null)
    {
        if (context.Request.Headers["Last-Event-ID"].Count > 1 || context.Request.Query["after"].Count > 1)
            throw new AdminFailure("event_cursor_invalid", 400, "只能提供一个事件标识。");
        // EventSource retains the original URL; its reconnect header must override that URL's initial cursor.
        string? header = lastEventId ?? context.Request.Headers["Last-Event-ID"].FirstOrDefault();
        string? cursor = string.IsNullOrEmpty(header) ? after : header;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        CancellationToken token = cancelled.Token;
        JobEventPageResponse page = await ReadAsync(context.User, id, cursor, token);
        IDisposable lease;
        try { lease = connections.Acquire(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!); }
        catch (AdminFailure failure) when (failure.Status == 429)
        {
            context.Response.Headers.RetryAfter = "3";
            await ApiProblems.WriteAsync(context, failure.Status, failure.Code, failure.Message);
            return;
        }
        using (lease)
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var elapsed = Stopwatch.StartNew();
            TimeSpan lastWrite = TimeSpan.Zero;
            bool initial = true;
            try
            {
                await WriteFrameAsync(context, "retry: 3000\n\n", token);
                while (elapsed.Elapsed < TimeSpan.FromSeconds(options.JobStreamLifetimeSeconds))
                {
                    bool heartbeat = elapsed.Elapsed - lastWrite >= TimeSpan.FromSeconds(options.JobStreamHeartbeatSeconds);
                    if (initial || page.Reset || page.Events.Count > 0 || heartbeat)
                    {
                        var frame = new StringBuilder();
                        foreach (var item in page.Events) Append(frame, "job", item.Id, item);
                        if (page.Reset)
                            Append(frame, initial && string.IsNullOrEmpty(cursor) ? "snapshot" : "reset", page.Cursor, page.Snapshot);
                        else Append(frame, "snapshot", null, page.Snapshot);
                        if (heartbeat) frame.Append(": heartbeat\n\n");
                        await WriteFrameAsync(context, frame.ToString(), token);
                        lastWrite = elapsed.Elapsed;
                    }
                    initial = false; cursor = page.Cursor;
                    if (!page.HasMore) await Task.Delay(options.JobStreamPollMilliseconds, token);
                    page = await ReadAsync(context.User, id, cursor, token);
                }
            }
            catch (AdminFailure failure) when (context.Response.HasStarted)
            {
                try
                {
                    var frame = new StringBuilder(); Append(frame, "closed", null, new { failure.Code });
                    await WriteFrameAsync(context, frame.ToString(), token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { context.Abort(); }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { context.Abort(); }
        }
    }

    private async Task<JobEventPageResponse> ReadAsync(ClaimsPrincipal principal, string id, string? cursor, CancellationToken token)
    {
        // No context, transaction, or unbounded subscriber queue survives a slow network write.
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobEventQuery>().ReadAsync(principal, id, cursor, 32, token);
    }

    private void Append<T>(StringBuilder frame, string kind, string? id, T data)
    {
        frame.Append("event: ").Append(kind).Append('\n');
        if (id != null) frame.Append("id: ").Append(id).Append('\n');
        frame.Append("data: ").Append(JsonSerializer.Serialize(data, json.Value.SerializerOptions)).Append("\n\n");
    }

    private async Task WriteFrameAsync(HttpContext context, string frame, CancellationToken token)
    {
        using var write = CancellationTokenSource.CreateLinkedTokenSource(token);
        write.CancelAfter(TimeSpan.FromSeconds(options.JobStreamWriteTimeoutSeconds));
        try
        {
            await context.Response.WriteAsync(frame, write.Token).WaitAsync(write.Token);
            await context.Response.Body.FlushAsync(write.Token).WaitAsync(write.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Task stream write deadline exceeded."); }
    }
}
