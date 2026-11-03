using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class DockerArchiveVerifier
{
    public static async Task VerifyAsync(string archive, string expectedImage, long maximumBytes, CancellationToken token = default)
    {
        await using var file = File.OpenRead(archive); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip); long total = 0; bool found = false; TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(false, token)) != null)
        {
            total = checked(total + entry.Length);
            string path = entry.Name.TrimEnd('/');
            if (total > maximumBytes || !ServerPackageVerifier.SafePath(path)
                || entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("镜像归档体积或条目不安全。");
            if (entry.Name != "manifest.json") continue;
            if (found || entry.Length > 1024 * 1024) throw new InvalidDataException("镜像清单无效。");
            using var manifest = await JsonDocument.ParseAsync(entry.DataStream!, cancellationToken: token);
            var images = manifest.RootElement;
            if (images.GetArrayLength() != 1 || images[0].GetProperty("RepoTags").GetArrayLength() != 1
                || images[0].GetProperty("RepoTags")[0].GetString() != expectedImage)
                throw new InvalidDataException("镜像归档包含未允许的标签或额外镜像。");
            found = true;
        }
        if (!found) throw new InvalidDataException("镜像归档缺少 Docker save 清单。");
    }
}
