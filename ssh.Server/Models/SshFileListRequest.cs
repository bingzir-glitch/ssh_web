namespace ssh.Server.Models;

public sealed class SshFileListRequest
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string? Path { get; init; }

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

        return errors;
    }
}
