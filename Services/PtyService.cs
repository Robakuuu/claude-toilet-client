using System.Collections.Concurrent;
using System.Text;
using ClaudeToiletClient.Native;

namespace ClaudeToiletClient.Services;

public class PtySession : IDisposable
{
    public string Id { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public int MasterFd { get; init; }
    public int ChildPid { get; init; }
    public FileStream ReaderStream { get; init; } = null!;
    public FileStream WriterStream { get; init; } = null!;
    public CancellationTokenSource Cts { get; } = new();
    public Task? ReaderTask { get; set; }
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
    private readonly ConcurrentDictionary<string, string> _connectionSessions = new();
    private readonly ILogger<PtyService> _logger;
    private readonly IConfiguration _config;

    public PtyService(ILogger<PtyService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public string CreateSession(
        string connectionId,
        string target,
        int cols,
        int rows,
        Func<string, Task> outputCallback)
    {
        var hostUser = _config.GetValue<string>("HostUser")
            ?? Environment.GetEnvironmentVariable("HOST_USER")
            ?? "root";
        var defaultShell = _config.GetValue<string>("DefaultShell") ?? "/bin/bash";

        // All commands use nsenter to run on the host, not inside the container
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
            // Format: pty:<pts_num>:<cwd>
            // Spawn a normal interactive shell, then cd to the target directory
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
            ConnectionId = connectionId,
            MasterFd = masterFd,
            ChildPid = childPid,
            ReaderStream = NativePty.CreateReadStream(masterFd),
            WriterStream = NativePty.CreateWriteStream(masterFd),
        };

        session.ReaderTask = Task.Run(() => ReadOutputLoop(session, outputCallback));

        _sessions[sessionId] = session;
        _connectionSessions[connectionId] = sessionId;

        // If we need to cd to a specific directory, send it after a short delay
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

    public void DisposeSession(string connectionId)
    {
        if (!_connectionSessions.TryRemove(connectionId, out var sessionId)) return;
        if (!_sessions.TryRemove(sessionId, out var session)) return;

        _logger.LogInformation("Disposing PTY session: id={SessionId}", sessionId);
        session.Dispose();
    }

    public bool HasSession(string connectionId)
    {
        return _connectionSessions.ContainsKey(connectionId);
    }

    private async Task ReadOutputLoop(PtySession session, Func<string, Task> outputCallback)
    {
        var buffer = new byte[4096];
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                // Synchronous read on dedicated thread (PTY fds don't support async)
                int bytesRead = await Task.Run(() =>
                {
                    try { return session.ReaderStream.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { return 0; }
                });

                if (bytesRead <= 0) break;

                string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                await outputCallback(output);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader loop error for session {SessionId}", session.Id);
        }

        _logger.LogInformation("Reader loop ended for session {SessionId}", session.Id);
    }
}
