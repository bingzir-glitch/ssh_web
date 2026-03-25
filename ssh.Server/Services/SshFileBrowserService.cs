using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using ssh.Server.Models;

namespace ssh.Server.Services;

public sealed class SshFileBrowserService
{
    private readonly SshUploadProgressStore _uploadProgressStore;

    public SshFileBrowserService(SshUploadProgressStore uploadProgressStore)
    {
        _uploadProgressStore = uploadProgressStore;
    }

    public Task<SshDirectoryListing> ListAsync(SshFileListRequest request, CancellationToken cancellationToken)
    {
        // SSH.NET 的文件操作是同步 API，这里包一层 Task 方便和 ASP.NET 异步接口对齐。
        return Task.Run(() => List(request), cancellationToken);
    }

    public Task ApplyActionAsync(SshFileActionRequest request, CancellationToken cancellationToken)
    {
        return Task.Run(() => ApplyAction(request), cancellationToken);
    }

    public Task<SshFileUploadResult> UploadAsync(SshFileUploadRequest request, CancellationToken cancellationToken)
    {
        return Task.Run(() => Upload(request, cancellationToken), cancellationToken);
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

        return new SshDirectoryListing(
            Path: currentPath,
            ParentPath: GetParentPath(currentPath),
            Entries: entries);
    }

    private static void ApplyAction(SshFileActionRequest request)
    {
        using var client = CreateClient(request.Host, request.Port, request.Username, request.Password);
        client.Connect();

        var normalizedAction = request.Action.Trim().ToLowerInvariant();
        var normalizedPath = NormalizePath(request.Path);

        switch (normalizedAction)
        {
            case "rename":
            {
                var parentPath = GetParentPath(normalizedPath) ?? "/";
                var targetPath = CombinePath(parentPath, request.Name!);
                client.RenameFile(normalizedPath, targetPath);
                break;
            }
            case "delete":
                DeleteEntryFast(request.Host, request.Port, request.Username, request.Password, normalizedPath);
                break;
            case "create-file":
            {
                var filePath = CombinePath(NormalizeDirectoryPath(normalizedPath), request.Name!);
                using var file = client.Create(filePath);
                file.Flush();
                break;
            }
            case "create-directory":
            {
                var directoryPath = CombinePath(NormalizeDirectoryPath(normalizedPath), request.Name!);
                EnsureDirectoryExists(client, directoryPath);
                break;
            }
        }
    }

    private SshFileUploadResult Upload(SshFileUploadRequest request, CancellationToken cancellationToken)
    {
        var uploadId = string.IsNullOrWhiteSpace(request.UploadId)
            ? Guid.NewGuid().ToString("N")
            : request.UploadId.Trim();
        var totalBytes = request.Files.Sum(file => file.File?.Length ?? 0);

        _uploadProgressStore.Start(uploadId, totalBytes);

        using var client = CreateClient(request.Host, request.Port, request.Username, request.Password);

        try
        {
            client.Connect();

            var targetRootPath = NormalizeDirectoryPath(string.IsNullOrWhiteSpace(request.Path) ? client.WorkingDirectory : request.Path);
            if (!client.Exists(targetRootPath))
            {
                throw new SftpPathNotFoundException("目标目录不存在。");
            }

            var targetRootEntry = client.Get(targetRootPath);
            if (!targetRootEntry.IsDirectory)
            {
                throw new ArgumentException("上传目标必须是文件夹。");
            }

            var normalizedDirectories = request.Directories
                .Select(NormalizeRelativePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Count(ch => ch == '/'))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var relativeDirectory in normalizedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryPath = CombinePath(targetRootPath, relativeDirectory);
                EnsureDirectoryExists(client, directoryPath);
            }

            long writtenBytes = 0;

            foreach (var uploadFile in request.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeRelativePath(uploadFile.RelativePath);
                var targetFilePath = CombinePath(targetRootPath, relativePath);
                var parentDirectory = GetParentPath(targetFilePath) ?? "/";

                EnsureDirectoryExists(client, parentDirectory);
                DeleteExistingEntryIfNeeded(client, targetFilePath);

                using var source = uploadFile.File.OpenReadStream();
                long uploadedForCurrentFile = 0;
                try
                {
                    client.UploadFile(source, targetFilePath, uploadedBytes =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var uploadedNow = (long)uploadedBytes;
                        var bytesDelta = Math.Max(0, uploadedNow - uploadedForCurrentFile);
                        if (bytesDelta <= 0)
                        {
                            return;
                        }

                        uploadedForCurrentFile = uploadedNow;
                        writtenBytes += bytesDelta;
                        _uploadProgressStore.Report(uploadId, writtenBytes, totalBytes);
                    });
                }
                catch (OperationCanceledException)
                {
                    DeleteExistingEntryIfNeeded(client, targetFilePath);
                    _uploadProgressStore.Cancel(uploadId);
                    throw;
                }
            }

            var message = $"已上传 {request.Files.Count} 个文件";
            if (normalizedDirectories.Length > 0)
            {
                message += $"，同步 {normalizedDirectories.Length} 个文件夹";
            }

            message += "。";
            _uploadProgressStore.Complete(uploadId, message);

            return new SshFileUploadResult(
                Path: targetRootPath,
                FileCount: request.Files.Count,
                DirectoryCount: normalizedDirectories.Length,
                Message: message);
        }
        catch (OperationCanceledException)
        {
            _uploadProgressStore.Cancel(uploadId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _uploadProgressStore.Fail(uploadId, ex.Message);
            throw;
        }
    }

    private static SftpClient CreateClient(string host, int port, string username, string? password)
    {
        // 文件浏览器每次请求都新建一个短连接，避免长期占用 SFTP 连接。
        var client = new SftpClient(host.Trim(), port, username.Trim(), password ?? string.Empty)
        {
            OperationTimeout = TimeSpan.FromSeconds(15),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = 256 * 1024u,
            ConnectionInfo =
            {
                Timeout = TimeSpan.FromSeconds(15)
            }
        };

        return client;
    }

    private static void DeleteEntryFast(string host, int port, string username, string? password, string path)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath == "/")
        {
            throw new ArgumentException("根目录不允许删除。");
        }

        using var client = new SshClient(host.Trim(), port, username.Trim(), password ?? string.Empty)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            ConnectionInfo =
            {
                Timeout = TimeSpan.FromSeconds(15)
            }
        };

        client.Connect();

        var command = client.CreateCommand($"rm -rf -- {EscapeShellArgument(normalizedPath)}");
        command.CommandTimeout = TimeSpan.FromSeconds(60);
        command.Execute();

        if (command.ExitStatus != 0 && !string.IsNullOrWhiteSpace(command.Error))
        {
            throw new ArgumentException($"强制删除失败：{command.Error.Trim()}");
        }
    }

    private static void DeleteExistingEntryIfNeeded(SftpClient client, string path)
    {
        if (!SafeExists(client, path))
        {
            return;
        }

        DeleteEntry(client, path);
    }

    private static void DeleteEntry(SftpClient client, string path)
    {
        if (!TryGetEntry(client, path, out var entry))
        {
            return;
        }

        if (entry.IsDirectory)
        {
            DeleteDirectoryRecursive(client, path);
            return;
        }

        TryDeleteFile(client, path);
    }

    private static void DeleteDirectoryRecursive(SftpClient client, string directoryPath)
    {
        ISftpFile[] entries;

        try
        {
            entries = client
                .ListDirectory(directoryPath)
                .Where(item => item.Name is not "." and not "..")
                .ToArray();
        }
        catch (SftpPathNotFoundException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var entryPath = NormalizePath(entry.FullName);

            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(client, entryPath);
                continue;
            }

            TryDeleteFile(client, entryPath);
        }

        TryDeleteDirectory(client, directoryPath);
    }

    private static bool SafeExists(SftpClient client, string path)
    {
        try
        {
            return client.Exists(path);
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetEntry(SftpClient client, string path, out ISftpFile entry)
    {
        try
        {
            entry = client.Get(path);
            return true;
        }
        catch (SftpPathNotFoundException)
        {
            entry = null!;
            return false;
        }
    }

    private static void TryDeleteFile(SftpClient client, string path)
    {
        try
        {
            client.DeleteFile(path);
        }
        catch (SftpPathNotFoundException)
        {
        }
    }

    private static void TryDeleteDirectory(SftpClient client, string path)
    {
        try
        {
            client.DeleteDirectory(path);
        }
        catch (SftpPathNotFoundException)
        {
        }
    }

    private static void EnsureDirectoryExists(SftpClient client, string directoryPath)
    {
        var normalizedDirectory = NormalizeDirectoryPath(directoryPath);
        if (normalizedDirectory == "/")
        {
            return;
        }

        var segments = normalizedDirectory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var currentPath = "/";
        foreach (var segment in segments)
        {
            currentPath = currentPath == "/" ? $"/{segment}" : $"{currentPath}/{segment}";

            if (!client.Exists(currentPath))
            {
                client.CreateDirectory(currentPath);
                continue;
            }

            var existingEntry = client.Get(currentPath);
            if (existingEntry.IsDirectory)
            {
                continue;
            }

            client.DeleteFile(currentPath);
            client.CreateDirectory(currentPath);
        }
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

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("上传路径不能为空。");
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not "." and not "..")
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("上传路径不合法。");
        }

        return string.Join('/', segments);
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

    private static string EscapeShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
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
