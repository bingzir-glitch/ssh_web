using System.Collections.Concurrent;

namespace ssh.Server.Services;

public sealed class SshUploadProgressStore : IDisposable
{
    private static readonly TimeSpan ProgressLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, UploadProgressSnapshot> _progress = new();
    private readonly Timer _cleanupTimer;

    public SshUploadProgressStore()
    {
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public UploadProgressSnapshot Start(string uploadId, long totalBytes)
    {
        var snapshot = new UploadProgressSnapshot(
            UploadId: uploadId,
            Stage: "syncing",
            WrittenBytes: 0,
            TotalBytes: Math.Max(0, totalBytes),
            UpdatedAt: DateTimeOffset.UtcNow,
            IsCompleted: false,
            IsCancelled: false,
            Message: "正在写入远端服务器...");

        _progress[uploadId] = snapshot;
        return snapshot;
    }

    public void Report(string uploadId, long writtenBytes, long totalBytes)
    {
        if (!_progress.TryGetValue(uploadId, out var current))
        {
            return;
        }

        _progress[uploadId] = current with
        {
            WrittenBytes = Math.Clamp(writtenBytes, 0, Math.Max(0, totalBytes)),
            TotalBytes = Math.Max(0, totalBytes),
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = "正在写入远端服务器..."
        };
    }

    public void Complete(string uploadId, string? message = null)
    {
        if (!_progress.TryGetValue(uploadId, out var current))
        {
            return;
        }

        _progress[uploadId] = current with
        {
            Stage = "completed",
            WrittenBytes = current.TotalBytes,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsCompleted = true,
            Message = message ?? "上传完成。"
        };
    }

    public void Cancel(string uploadId)
    {
        if (!_progress.TryGetValue(uploadId, out var current))
        {
            return;
        }

        _progress[uploadId] = current with
        {
            Stage = "cancelled",
            UpdatedAt = DateTimeOffset.UtcNow,
            IsCancelled = true,
            Message = "上传已取消。"
        };
    }

    public void Fail(string uploadId, string? message = null)
    {
        if (!_progress.TryGetValue(uploadId, out var current))
        {
            return;
        }

        _progress[uploadId] = current with
        {
            Stage = "failed",
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = message ?? "上传失败。"
        };
    }

    public bool TryGet(string uploadId, out UploadProgressSnapshot snapshot)
    {
        return _progress.TryGetValue(uploadId, out snapshot!);
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var item in _progress)
        {
            if (now - item.Value.UpdatedAt <= ProgressLifetime)
            {
                continue;
            }

            _progress.TryRemove(item.Key, out _);
        }
    }
}

public sealed record UploadProgressSnapshot(
    string UploadId,
    string Stage,
    long WrittenBytes,
    long TotalBytes,
    DateTimeOffset UpdatedAt,
    bool IsCompleted,
    bool IsCancelled,
    string Message);
