using System.Text.Json;
using ssh.Server.Models;

namespace ssh.Server.Services;

public sealed class SavedSshConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath;

    public SavedSshConnectionStore(IHostEnvironment environment)
    {
        // 直接保存在服务端项目目录下，开发和部署时都更容易找到。
        _filePath = Path.Combine(environment.ContentRootPath, "data", "saved-connections.json");
    }

    public async Task<IReadOnlyList<SavedSshConnection>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            return await ReadAllUnsafeAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SavedSshConnection>> ReplaceAllAsync(
        IReadOnlyList<SavedSshConnection> connections,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var normalizedConnections = connections
                .Select(connection => connection.Normalize())
                .ToArray();

            var directoryPath = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var tempFilePath = $"{_filePath}.tmp";
            await using (var stream = File.Create(tempFilePath))
            {
                await JsonSerializer.SerializeAsync(stream, normalizedConnections, JsonOptions, cancellationToken);
            }

            File.Move(tempFilePath, _filePath, overwrite: true);
            return normalizedConnections;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<SavedSshConnection>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<SavedSshConnection>();
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var connections = await JsonSerializer.DeserializeAsync<List<SavedSshConnection>>(
                stream,
                JsonOptions,
                cancellationToken);

            return connections?
                .Select(connection => connection.Normalize())
                .ToArray() ?? Array.Empty<SavedSshConnection>();
        }
        catch (JsonException)
        {
            return Array.Empty<SavedSshConnection>();
        }
    }
}
