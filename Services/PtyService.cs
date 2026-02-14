using System.Collections.Concurrent;
using System.Text;
using ClaudeToiletClient.Native;

namespace ClaudeToiletClient.Services;

public record PtySessionInfo(string Id, string Target, DateTime CreatedAt, bool HasClient);

public class PtySession : IDisposable
{
    public string Id { get; init; } = string.Empty;
    public int MasterFd { get; init; }
    public int ChildPid { get; init; }
    public FileStream ReaderStream { get; init; } = null!;
    public FileStream WriterStream { get; init; } = null!;
    public CancellationTokenSource Cts { get; } = new();
    public Task? ReaderTask { get; set; }
    public ScrollbackBuffer Scrollback { get; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string Target { get; init; } = string.Empty;

    private readonly object _callbackLock = new();
    private Func<string, Task>? _outputCallback;

    public void AttachClient(Func<string, Task> callback)
    {
        lock (_callbackLock) { _outputCallback = callback; }
    }

    public void DetachClient()
    {
        lock (_callbackLock) { _outputCallback = null; }
    }

    public Func<string, Task>? GetOutputCallback()
    {
        lock (_callbackLock) { return _outputCallback; }
    }

    public bool IsAlive => NativePty.IsProcessRunning(ChildPid);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachClient();
        Cts.Cancel();

        try { ReaderStream.Dispose(); } catch { }
        try { WriterStream.Dispose(); } catch { }

        if (NativePty.IsProcessRunning(ChildPid))
        {
            NativePty.KillProcess(ChildPid);
        }

        NativePty.CloseFd(MasterFd);
        Cts.Dispose();
    }
}

public class PtyService
{
    private readonly ConcurrentDictionary<string, PtySession> _sessions = new();
    private readonly ILogger<PtyService> _logger;
    private readonly IConfiguration _config;
    private readonly Timer _cleanupTimer;

    public PtyService(ILogger<PtyService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _cleanupTimer = new Timer(CleanupDeadSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string CreateSession(string target, int cols, int rows)
    {
        var hostUser = _config.GetValue<string>("HostUser")
            ?? Environment.GetEnvironmentVariable("HOST_USER")
            ?? "root";

        string command = "nsenter";
        string[] args;
        string? initialCwd = null;

        if (target.StartsWith("tmux:"))
        {
            var sessionName = target[5..];
            args = ["-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
                    "su", "-", hostUser, "-c", $"tmux attach-session -t {sessionName}"];
        }
        else if (target.StartsWith("pty:"))
        {
            var parts = target.Split(':', 3);
            initialCwd = parts.Length >= 3 ? parts[2] : null;
            args = ["-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
                    "su", "-", hostUser];
        }
        else
        {
            args = ["-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
                    "su", "-", hostUser];
        }

        _logger.LogInformation("Creating PTY session: command={Command}, target={Target}", command, target);

        var (masterFd, childPid) = NativePty.ForkWithPty(command, args, cols, rows);

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new PtySession
        {
            Id = sessionId,
            MasterFd = masterFd,
            ChildPid = childPid,
            ReaderStream = NativePty.CreateReadStream(masterFd),
            WriterStream = NativePty.CreateWriteStream(masterFd),
            Target = target,
        };

        session.ReaderTask = Task.Run(() => ReadOutputLoop(session));

        _sessions[sessionId] = session;

        if (initialCwd != null)
        {
            var cwd = initialCwd;
            var sid = sessionId;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Write(sid, $"cd '{cwd}'\n");
            });
        }

        _logger.LogInformation("PTY session created: id={SessionId}, pid={Pid}", sessionId, childPid);
        return sessionId;
    }

    public string? AttachToSession(string sessionId, Func<string, Task> outputCallback)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        if (!session.IsAlive)
        {
            CleanupSession(sessionId);
            return null;
        }

        session.AttachClient(outputCallback);
        return sessionId;
    }

    public void DetachFromSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.DetachClient();
        }
    }

    public void DestroySession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        _logger.LogInformation("Destroying PTY session: id={SessionId}", sessionId);
        session.Dispose();
    }

    public string? GetScrollback(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return session.Scrollback.GetContents();
        return null;
    }

    public IReadOnlyList<PtySessionInfo> GetActiveSessions()
    {
        return _sessions.Values
            .Where(s => s.IsAlive)
            .Select(s => new PtySessionInfo(s.Id, s.Target, s.CreatedAt, s.GetOutputCallback() != null))
            .ToList();
    }

    public void Write(string sessionId, string data)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var bytes = Encoding.UTF8.GetBytes(data);
        try
        {
            session.WriterStream.Write(bytes, 0, bytes.Length);
            session.WriterStream.Flush();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Write failed for session {SessionId}", sessionId);
        }
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        NativePty.SetWindowSize(session.MasterFd, cols, rows);
    }

    private async Task ReadOutputLoop(PtySession session)
    {
        var buffer = new byte[4096];
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                int bytesRead = await Task.Run(() =>
                {
                    try { return session.ReaderStream.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { return 0; }
                });

                if (bytesRead <= 0) break;

                string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Always buffer output
                session.Scrollback.Append(output);

                // Forward to attached client if any
                var callback = session.GetOutputCallback();
                if (callback != null)
                {
                    try
                    {
                        await callback(output);
                    }
                    catch
                    {
                        // Client gone, detach silently
                        session.DetachClient();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader loop error for session {SessionId}", session.Id);
        }

        _logger.LogInformation("Reader loop ended for session {SessionId} (process exited)", session.Id);
        CleanupSession(session.Id);
    }

    private void CleanupSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            _logger.LogInformation("Cleaning up dead session: id={SessionId}", sessionId);
            session.Dispose();
        }
    }

    private void CleanupDeadSessions(object? state)
    {
        foreach (var kvp in _sessions)
        {
            if (!kvp.Value.IsAlive)
            {
                _logger.LogInformation("Auto-cleaning dead session {SessionId}", kvp.Key);
                CleanupSession(kvp.Key);
            }
        }
    }
}
