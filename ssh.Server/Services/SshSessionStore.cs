using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using ssh.Server.Models;

namespace ssh.Server.Services;

public sealed class SshSessionStore : IDisposable
{
    private static readonly TimeSpan PendingSessionLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, PendingSession> _pendingSessions = new();
    private readonly Timer _cleanupTimer;

    public SshSessionStore()
    {
        _cleanupTimer = new Timer(_ => CleanupExpiredSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<SshSession> CreateAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        CleanupExpiredSessions();

        var session = await Task.Run(() => OpenSession(request), cancellationToken);
        _pendingSessions[session.Id] = new PendingSession(session, DateTimeOffset.UtcNow);
        return session;
    }

    public bool TryTake(string sessionId, out SshSession? session)
    {
        CleanupExpiredSessions();

        if (_pendingSessions.TryRemove(sessionId, out var pending))
        {
            session = pending.Session;
            return true;
        }

        session = null;
        return false;
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();

        foreach (var pending in _pendingSessions.Values)
        {
            pending.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _pendingSessions.Clear();
    }

    private static SshSession OpenSession(SshConnectRequest request)
    {
        var host = request.Host.Trim();
        var username = request.Username.Trim();
        var terminalColumns = Math.Max(20, request.Columns);
        var terminalRows = Math.Max(10, request.Rows);

        var connectionInfo = CreateConnectionInfo(host, request.Port, username, request.Password, request.PrivateKey, request.PrivateKeyPassphrase);
        connectionInfo.Timeout = TimeSpan.FromSeconds(15);

        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            client.Connect();
            var shell = client.CreateShellStream("xterm-256color", (uint)terminalColumns, (uint)terminalRows, 0, 0, 4096);
            return new SshSession(Guid.NewGuid().ToString("N"), host, username, client, shell);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static Renci.SshNet.ConnectionInfo CreateConnectionInfo(
        string host,
        int port,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase)
    {
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(password))
        {
            methods.Add(new PasswordAuthenticationMethod(username, password));

            var keyboardInteractiveMethod = new KeyboardInteractiveAuthenticationMethod(username);
            keyboardInteractiveMethod.AuthenticationPrompt += (_, args) =>
            {
                foreach (var prompt in args.Prompts)
                {
                    if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        prompt.Response = password;
                    }
                }
            };

            methods.Add(keyboardInteractiveMethod);
        }

        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            using var privateKeyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
            PrivateKeyFile privateKeyFile;

            try
            {
                privateKeyFile = string.IsNullOrWhiteSpace(privateKeyPassphrase)
                    ? new PrivateKeyFile(privateKeyStream)
                    : new PrivateKeyFile(privateKeyStream, privateKeyPassphrase);
            }
            catch (SshException ex)
            {
                throw new ArgumentException($"私钥读取失败：{ex.Message}", ex);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(username, privateKeyFile));
        }

        if (methods.Count == 0)
        {
            throw new ArgumentException("密码和私钥至少需要提供一种。");
        }

        return new Renci.SshNet.ConnectionInfo(host, port, username, methods.ToArray());
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in _pendingSessions)
        {
            if (now - entry.Value.CreatedAt <= PendingSessionLifetime)
            {
                continue;
            }

            if (_pendingSessions.TryRemove(entry.Key, out var expired))
            {
                expired.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private sealed record PendingSession(SshSession Session, DateTimeOffset CreatedAt);
}
