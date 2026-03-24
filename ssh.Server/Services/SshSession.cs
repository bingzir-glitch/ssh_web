using Renci.SshNet;

namespace ssh.Server.Services;

public sealed class SshSession : IAsyncDisposable
{
    public SshSession(string id, string host, string username, SshClient client, ShellStream shell)
    {
        Id = id;
        this.host = host;
        this.username = username;
        Client = client;
        Shell = shell;
    }

    public string Id { get; }

    public string host { get; }

    public string username { get; }

    public SshClient Client { get; }

    public ShellStream Shell { get; }

    public bool IsConnected => Client.IsConnected && Shell.CanRead && Shell.CanWrite;

    public void Resize(int? columns, int? rows)
    {
        var safeColumns = Math.Max(20, columns ?? 120);
        var safeRows = Math.Max(10, rows ?? 32);

        Shell.ChangeWindowSize((uint)safeColumns, (uint)safeRows, 0, 0);
    }

    public ValueTask DisposeAsync()
    {
        Shell.Dispose();
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}
