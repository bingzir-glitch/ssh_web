using Renci.SshNet;
using Renci.SshNet.Common;
using ssh.Server.Models;

namespace ssh.Server.Services;

public sealed class SshFileBrowserService
{
    public Task<SshDirectoryListing> ListAsync(SshFileListRequest request, CancellationToken cancellationToken)
    {
        // SSH.NET 的文件操作是同步 API，这里包一层 Task 方便和 ASP.NET 异步接口对齐。
        return Task.Run(() => List(request), cancellationToken);
    }

    public Task ApplyActionAsync(SshFileActionRequest request, CancellationToken cancellationToken)
    {
        return Task.Run(() => ApplyAction(request), cancellationToken);
    }

    private static SshDirectoryListing List(SshFileListRequest request)
    {
        using var client = CreateClient(request.Host, request.Port, request.Username, request.Password);
        client.Connect();

        // 没传路径时回落到远端当前工作目录，前端传了路径则优先使用前端目标目录。
        var currentPath = NormalizeDirectoryPath(string.IsNullOrWhiteSpace(request.Path) ? client.WorkingDirectory : request.Path);

        if (!client.Exists(currentPath))
        {
            throw new SftpPathNotFoundException("目标目录不存在。");
        }

        var directory = client.Get(currentPath);
        if (!directory.IsDirectory)
        {
            throw new ArgumentException("目标路径不是文件夹。");
        }

        var entries = client
            .ListDirectory(currentPath)
            .Where(entry => entry.Name is not "." and not "..")
            .Select(entry => new SshFileEntry(
                Name: entry.Name,
                Path: NormalizePath(entry.FullName),
                IsDirectory: entry.IsDirectory,
                Size: entry.IsDirectory ? null : entry.Attributes.Size,
                TypeLabel: entry.IsDirectory ? "文件夹" : "文件",
                ModifiedAt: entry.Attributes.LastWriteTimeUtc))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // 目录排前面、名称再排序，前端可以直接拿来展示，不需要再额外整理。

        return new SshDirectoryListing(
            Path: currentPath,
            ParentPath: GetParentPath(currentPath),
            Entries: entries);
    }

    private static void ApplyAction(SshFileActionRequest request)
    {
        using var client = CreateClient(request.Host, request.Port, request.Username, request.Password);
        client.Connect();

        // 动作名和路径统一先做归一化，避免不同来源的大小写或斜杠形式影响处理。
        var normalizedAction = request.Action.Trim().ToLowerInvariant();
        var normalizedPath = NormalizePath(request.Path);

        switch (normalizedAction)
        {
            case "rename":
            {
                // 重命名实际是“同级目录下换一个新名字”。
                var parentPath = GetParentPath(normalizedPath) ?? "/";
                var targetPath = CombinePath(parentPath, request.Name!);
                client.RenameFile(normalizedPath, targetPath);
                break;
            }
            case "delete":
                DeleteEntry(client, normalizedPath);
                break;
            case "create-file":
            {
                var filePath = CombinePath(NormalizeDirectoryPath(normalizedPath), request.Name!);
                // Create 会直接返回流，哪怕是空文件也要尽快 flush 一次确保落盘。
                using var file = client.Create(filePath);
                file.Flush();
                break;
            }
            case "create-directory":
            {
                var directoryPath = CombinePath(NormalizeDirectoryPath(normalizedPath), request.Name!);
                client.CreateDirectory(directoryPath);
                break;
            }
        }
    }

    private static SftpClient CreateClient(string host, int port, string username, string? password)
    {
        // 文件浏览器每次请求都新建一个短连接，避免长期占用 SFTP 连接。
        var client = new SftpClient(host.Trim(), port, username.Trim(), password ?? string.Empty)
        {
            OperationTimeout = TimeSpan.FromSeconds(15),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            ConnectionInfo =
            {
                Timeout = TimeSpan.FromSeconds(15)
            }
        };

        return client;
    }

    private static void DeleteEntry(SftpClient client, string path)
    {
        var entry = client.Get(path);
        if (entry.IsDirectory)
        {
            // SSH.NET 不会帮我们递归删目录，所以目录删除需要手动向下清空。
            DeleteDirectoryRecursive(client, path);
            return;
        }

        client.DeleteFile(path);
    }

    private static void DeleteDirectoryRecursive(SftpClient client, string directoryPath)
    {
        foreach (var entry in client.ListDirectory(directoryPath).Where(item => item.Name is not "." and not ".."))
        {
            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(client, NormalizePath(entry.FullName));
            }
            else
            {
                client.DeleteFile(NormalizePath(entry.FullName));
            }
        }

        client.DeleteDirectory(directoryPath);
    }

    private static string NormalizeDirectoryPath(string? path)
    {
        var normalized = NormalizePath(path);
        return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
    }

    private static string NormalizePath(string? path)
    {
        // 前后端统一使用 / 开头、/ 分隔、且不带尾斜杠的绝对路径格式。
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            return "/";
        }

        normalized = normalized.StartsWith('/') ? normalized : $"/{normalized}";

        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static string? GetParentPath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/")
        {
            return null;
        }

        var lastSlashIndex = normalized.LastIndexOf('/');
        return lastSlashIndex <= 0 ? "/" : normalized[..lastSlashIndex];
    }

    private static string CombinePath(string directoryPath, string name)
    {
        var normalizedDirectory = NormalizeDirectoryPath(directoryPath);
        var normalizedName = name.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("名称不能为空。");
        }

        return normalizedDirectory == "/"
            ? $"/{normalizedName}"
            : $"{normalizedDirectory}/{normalizedName}";
    }
}

public sealed record SshDirectoryListing(string Path, string? ParentPath, IReadOnlyList<SshFileEntry> Entries);

public sealed record SshFileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    string TypeLabel,
    DateTimeOffset ModifiedAt);
