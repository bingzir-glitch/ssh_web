namespace ssh.Server.Models;

public sealed class SshFileActionRequest
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string Action { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string? Name { get; init; }

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedAction = Action.Trim().ToLowerInvariant();

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

        if (string.IsNullOrWhiteSpace(Path))
        {
            errors[nameof(Path)] = ["目标路径不能为空。"];
        }

        if (normalizedAction is not ("rename" or "delete" or "create-file" or "create-directory"))
        {
            errors[nameof(Action)] = ["不支持的文件操作。"];
        }

        if (normalizedAction is "rename" or "create-file" or "create-directory")
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                errors[nameof(Name)] = ["名称不能为空。"];
            }
            else if (Name.Contains('/') || Name.Contains('\\'))
            {
                errors[nameof(Name)] = ["名称不能包含路径分隔符。"];
            }
        }

        return errors;
    }
}
