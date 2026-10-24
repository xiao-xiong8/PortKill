using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

internal sealed class UpdateEventClient(HttpClient client, Uri uri)
{
    internal async Task RunAsync(Action hint, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            TimeSpan retry = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(attempt++, 6)) + Random.Shared.NextDouble());
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (response.Headers.RetryAfter?.Delta is { } delta && delta > retry) retry = delta;
                if (response.Headers.RetryAfter?.Date is { } date && date - DateTimeOffset.UtcNow > retry) retry = date - DateTimeOffset.UtcNow;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.NotImplemented or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    retry = TimeSpan.FromMinutes(30);
                else
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                        throw new InvalidDataException("更新事件响应类型无效。");
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    await ParseAsync(stream, hint, token, () => attempt = 0);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or FormatException)
            { UpdateLog.Write("UpdateEvents", ex); }
            await Task.Delay(retry > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : retry, token);
        }
    }

    internal static async Task ParseAsync(Stream stream, Action hint, CancellationToken token, Action? activity = null)
    {
        byte[] buffer = new byte[4096];
        var line = new List<byte>(256);
        var data = new StringBuilder();
        string eventName = "";
        int eventBytes = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(75));
            int read = await stream.ReadAsync(buffer, idle.Token);
            if (read == 0) return;
            activity?.Invoke();
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (value != 10)
                {
                    if (line.Count >= 4096) throw new InvalidDataException("更新事件行超限。");
                    line.Add(value); continue;
                }
                string text = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line)).TrimEnd('\r');
                eventBytes += line.Count + 1; line.Clear();
                if (eventBytes > 8192) throw new InvalidDataException("更新事件超限。");
                if (text.Length == 0)
                {
                    if (eventName == "release" && data.Length != 0)
                    {
                        var release = JsonSerializer.Deserialize(data.ToString(), ProtocolJsonContext.Default.ReleaseHint);
                        if (release is { Schema: 1, Channel: "stable", Sequence: > 0, Version.Length: <= 32 }
                            && Version.TryParse(release.Version, out _)) hint();
                    }
                    data.Clear(); eventName = ""; eventBytes = 0; continue;
                }
                if (text[0] == ':') continue;
                int colon = text.IndexOf(':');
                string field = colon < 0 ? text : text[..colon];
                string content = colon < 0 ? "" : text[(colon + 1)..];
                if (content.StartsWith(' ')) content = content[1..];
                if (field == "event") eventName = content;
                if (field == "data") { if (data.Length != 0) data.Append('\n'); data.Append(content); }
            }
        }
    }
}
