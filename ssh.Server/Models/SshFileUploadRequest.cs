using Microsoft.AspNetCore.Http;

namespace ssh.Server.Models;

public sealed class SshFileUploadRequest
{
    public string UploadId { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string? Path { get; init; }

    public IReadOnlyList<SshUploadFileItem> Files { get; init; } = Array.Empty<SshUploadFileItem>();

    public IReadOnlyList<string> Directories { get; init; } = Array.Empty<string>();

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            errors[nameof(Host)] = ["主机地址不能为空。"];
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            errors[nameof(Username)] = ["用户名不能为空。"];
        }

        if (Port is < 1 or > 65535)
        {
            errors[nameof(Port)] = ["端口必须在 1 到 65535 之间。"];
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            errors[nameof(Password)] = ["密码不能为空。"];
        }

        if (Files.Count == 0 && Directories.Count == 0)
        {
            errors[nameof(Files)] = ["请至少上传一个文件或文件夹。"];
        }

        for (var index = 0; index < Files.Count; index++)
        {
            var file = Files[index];
            if (file.File is null || file.File.Length < 0)
            {
                errors[$"{nameof(Files)}[{index}]"] = ["上传文件无效。"];
                continue;
            }

            if (!IsSafeRelativePath(file.RelativePath))
            {
                errors[$"{nameof(Files)}[{index}].{nameof(SshUploadFileItem.RelativePath)}"] = ["文件路径不合法。"];
            }
        }

        for (var index = 0; index < Directories.Count; index++)
        {
            if (!IsSafeRelativePath(Directories[index]))
            {
                errors[$"{nameof(Directories)}[{index}]"] = ["文件夹路径不合法。"];
            }
        }

        return errors;
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }
}

public sealed class SshUploadFileItem
{
    public IFormFile File { get; init; } = default!;

    public string RelativePath { get; init; } = string.Empty;
}

public sealed record SshFileUploadResult(
    string Path,
    int FileCount,
    int DirectoryCount,
    string Message);
