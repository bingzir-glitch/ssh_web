namespace ssh.Server.Models;

public sealed class SshConnectRequest
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string? PrivateKey { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 32;

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

        if (string.IsNullOrWhiteSpace(Password) && string.IsNullOrWhiteSpace(PrivateKey))
        {
            errors[nameof(Password)] = ["密码和私钥至少需要提供一种。"];
        }

        if (Columns < 20)
        {
            errors[nameof(Columns)] = ["终端列数不能小于 20。"];
        }

        if (Rows < 10)
        {
            errors[nameof(Rows)] = ["终端行数不能小于 10。"];
        }

        return errors;
    }
}
