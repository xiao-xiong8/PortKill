using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class UploadTests
{
    private static async Task<JsonElement> CreateAsync(HttpClient client, long manifestBytes, long packageBytes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(manifestBytes, packageBytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        return await client.SendAsync(request);
    }

    private static ByteArrayContent Bytes(byte[] value)
    {
        var content = new ByteArrayContent(value); content.Headers.ContentType = new("application/octet-stream"); return content;
    }

    [Fact]
    public async Task UploadStreamsBothBoundedFilesAndCompletesWithoutReturningStoragePaths()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        byte[] manifest = [1, 2, 3, 4, 5], package = Enumerable.Range(0, 256_321).Select(value => (byte)value).ToArray();
        var created = await CreateAsync(app.Client, manifest.Length, package.Length);
        string id = created.GetProperty("id").GetString()!;
        using (var response = await PutAsync(app.Client, $"/api/v1/uploads/{id}/manifest", Bytes(manifest))) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await PutAsync(app.Client, $"/api/v1/uploads/{id}/package", Bytes(package))) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await app.Client.PostAsync($"/api/v1/uploads/{id}/complete", JsonContent.Create(new { })))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("uploaded", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }
        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/uploads/{id}");
        Assert.Equal(manifest.Length, status.GetProperty("manifestReceivedBytes").GetInt64());
        Assert.Equal(package.Length, status.GetProperty("packageReceivedBytes").GetInt64());
        Assert.DoesNotContain("storage", status.ToString(), StringComparison.OrdinalIgnoreCase);
        using var scope = app.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var record = await db.UploadSessions.SingleAsync(x => x.Id == id);
        string directory = Path.Combine(app.Root, "staging", record.StorageId);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.envelope")));
        Assert.Equal(package, await File.ReadAllBytesAsync(Path.Combine(directory, "package.zip")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(manifest)), record.ManifestSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), record.PackageSha256);
        Assert.Equal(UploadState.Uploaded, record.State);
    }

    [Fact]
    public async Task ChunkedBodyWithoutContentLengthUsesActualByteAccounting()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await CreateAsync(app.Client, 4, 1024); string id = created.GetProperty("id").GetString()!;
        using var content = new StreamContent(new NonSeekableStream(new MemoryStream([8, 7, 6, 5])));
        content.Headers.ContentType = new("application/octet-stream");
        Assert.Null(content.Headers.ContentLength);
        using var response = await PutAsync(app.Client, $"/api/v1/uploads/{id}/manifest", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("manifestReceivedBytes").GetInt64());
    }

    [Fact]
    public async Task WrongLengthOversizeAndIncompleteTransfersAreRejectedAndCleaned()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await CreateAsync(app.Client, 4, 4); string id = created.GetProperty("id").GetString()!;
        using (var response = await PutAsync(app.Client, $"/api/v1/uploads/{id}/manifest", Bytes([1, 2])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("upload_incomplete", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var response = await PutAsync(app.Client, $"/api/v1/uploads/{id}/manifest", Bytes([1, 2, 3, 4, 5])))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("upload_too_large", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/uploads/{id}")).GetProperty("manifestReceivedBytes").GetInt64());
        using var scope = app.App.Services.CreateScope(); var record = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().UploadSessions.SingleAsync(x => x.Id == id);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Root, "staging", record.StorageId), "*.part"));
        using (var response = await app.Client.PostAsync($"/api/v1/uploads/{id}/complete", JsonContent.Create(new { })))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ActualStreamFailureLeavesSessionRetryableAndRemovesPartialFile()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await CreateAsync(app.Client, 4, 4); string id = created.GetProperty("id").GetString()!;
        using (var writeScope = app.App.Services.CreateScope())
        {
            var users = writeScope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            string ownerId = (await users.FindByNameAsync("operator"))!.Id;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, ownerId), new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(AdminIdentity.PhaseClaim, SessionPhase.Full.ToString())
            ], AdminIdentity.Scheme));
            var service = writeScope.ServiceProvider.GetRequiredService<UploadManagementService>();
            await Assert.ThrowsAsync<IOException>(() => service.WritePartAsync(principal, id, UploadPart.Manifest,
                new FailingStream(), null, CancellationToken.None));
        }
        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/uploads/{id}");
        Assert.Equal("uploading", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("manifestReceivedBytes").GetInt64());
        using var scope = app.App.Services.CreateScope(); var record = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().UploadSessions.SingleAsync(x => x.Id == id);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Root, "staging", record.StorageId), ".incoming-*.part"));
    }

    [Fact]
    public async Task UploadQuotasAreEnforcedAndCancellationReleasesTheReservation()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var options = app.App.Services.GetRequiredService<AdminOptions>(); options.MaxActiveUploadsPerUser = 1;
        var first = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); string id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using (var denied = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            Assert.Equal("upload_quota_exceeded", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var cancel = await app.Client.DeleteAsync($"/api/v1/uploads/{id}")) Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16))).StatusCode);
    }

    [Fact]
    public async Task ConcurrentReservationRequestsCannotExceedConfiguredActiveSessionLimit()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().MaxActiveUploadsPerUser = 1;
        using var firstRequest = JsonContent.Create(new CreateUploadRequest(8, 16));
        using var secondRequest = JsonContent.Create(new CreateUploadRequest(8, 16));
        var responses = await Task.WhenAll(app.Client.PostAsync("/api/v1/uploads/", firstRequest),
            app.Client.PostAsync("/api/v1/uploads/", secondRequest));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);
        using var scope = app.App.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().UploadSessions.CountAsync(x => x.State == UploadState.Uploading));
    }

    [Fact]
    public async Task ConcurrentWritesToTheSamePartAreSerializedAndOnlyOneCanCommit()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await CreateAsync(app.Client, 4, 4); string id = created.GetProperty("id").GetString()!;
        var gate = new GatedStream([1, 2, 3, 4]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("userId").GetString()!),
            new Claim(ClaimTypes.Role, "Administrator"), new Claim(AdminIdentity.PhaseClaim, SessionPhase.Full.ToString())
        ], AdminIdentity.Scheme));
        Task<UploadSessionResponse> write;
        await using (var firstScope = app.App.Services.CreateAsyncScope())
        {
            var service = firstScope.ServiceProvider.GetRequiredService<UploadManagementService>();
            write = service.WritePartAsync(principal, id, UploadPart.Manifest, gate, null, CancellationToken.None);
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await using var secondScope = app.App.Services.CreateAsyncScope();
            var failure = await Assert.ThrowsAsync<AdminFailure>(() => secondScope.ServiceProvider.GetRequiredService<UploadManagementService>()
                .WritePartAsync(principal, id, UploadPart.Manifest, new MemoryStream([4, 3, 2, 1]), null, CancellationToken.None));
            Assert.Equal("upload_busy", failure.Code);
            gate.Continue.TrySetResult();
            Assert.Equal(4, (await write).ManifestReceivedBytes);
        }
        using var scope = app.App.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().UploadSessions.CountAsync(x => x.Id == id && x.ManifestReceivedBytes == 4));
    }

    [Fact]
    public async Task UploadAccessIsRoleLimitedAndForeignSessionIsNotFound()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16));
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        string otherId;
        using (var scope = app.App.Services.CreateScope())
        {
            var services = scope.ServiceProvider; var users = services.GetRequiredService<UserManager<AdminUser>>();
            var account = new AdminUser { UserName = "other-uploader", LockoutEnabled = true, TwoFactorEnabled = true, RecoveryCodesAcknowledged = true, CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            Assert.True((await users.CreateAsync(account, "Other-Uploader-Password!473")).Succeeded);
            Assert.True((await users.AddToRoleAsync(account, "Uploader")).Succeeded);
            Assert.True((await users.ResetAuthenticatorKeyAsync(account)).Succeeded);
            Assert.NotNull(await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 10));
        }
        using var other = app.NewClient();
        (await other.PostAsJsonAsync("/api/v1/auth/login", new { userName = "other-uploader", password = "Other-Uploader-Password!473" })).EnsureSuccessStatusCode();
        using (var me = await other.GetAsync("/api/v1/auth/me"))
        {
            var user = await me.Content.ReadFromJsonAsync<JsonElement>();
            otherId = user.GetProperty("userId").GetString()!;
        }
        string key;
        using (var scope = app.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(otherId))!))!;
        }
        (await other.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key) })).EnsureSuccessStatusCode();
        using (var foreign = await other.GetAsync($"/api/v1/uploads/{id}")) Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var foreignCancel = await other.DeleteAsync($"/api/v1/uploads/{id}")) Assert.Equal(HttpStatusCode.NotFound, foreignCancel.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16))).StatusCode);
        string viewerKey;
        using (var scope = app.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var viewerAccount = new AdminUser { UserName = "read-only", LockoutEnabled = true, TwoFactorEnabled = true,
                RecoveryCodesAcknowledged = true, CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            Assert.True((await users.CreateAsync(viewerAccount, "Read-Only-Password!483")).Succeeded);
            Assert.True((await users.AddToRoleAsync(viewerAccount, "Viewer")).Succeeded);
            Assert.True((await users.ResetAuthenticatorKeyAsync(viewerAccount)).Succeeded);
            Assert.NotNull(await users.GenerateNewTwoFactorRecoveryCodesAsync(viewerAccount, 10));
            viewerKey = (await users.GetAuthenticatorKeyAsync(viewerAccount))!;
        }
        using var viewer = app.NewClient();
        (await viewer.PostAsJsonAsync("/api/v1/auth/login", new { userName = "read-only", password = "Read-Only-Password!483" })).EnsureSuccessStatusCode();
        (await viewer.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(viewerKey) })).EnsureSuccessStatusCode();
        using (var denied = await viewer.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(8, 16))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task InvalidReservationsAreRejectedBeforeStorageAllocation()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using (var empty = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(0, 1))) Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var largeManifest = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(65_537, 1))) Assert.Equal(HttpStatusCode.BadRequest, largeManifest.StatusCode);
        using (var largePackage = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(1, 268_435_457))) Assert.Equal(HttpStatusCode.BadRequest, largePackage.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(app.Root, "staging")));
    }
}

internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

internal sealed class FailingStream : Stream
{
    private int _reads;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_reads++ != 0) throw new IOException("simulated source interruption");
        buffer[offset] = 1; return 1;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (_reads++ != 0) return ValueTask.FromException<int>(new IOException("simulated source interruption"));
        buffer.Span[0] = 1; return ValueTask.FromResult(1);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class GatedStream(byte[] value) : Stream
{
    private int _read;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (Interlocked.Increment(ref _read) != 1) return 0;
        Started.TrySetResult(); await Continue.Task.WaitAsync(token);
        value.CopyTo(buffer); return value.Length;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
