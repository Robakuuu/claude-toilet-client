using System.Diagnostics;

namespace ClaudeToiletClient.Services;

public enum HostSessionType
{
    NewShell,
    Tmux,
    ActiveTerminal,
}

public record HostSession(
    HostSessionType Type,
    string Id,
    string Label,
    string? Description = null);

public class HostSessionService
{
    private readonly string _hostUser;
    private readonly ILogger<HostSessionService> _logger;

    public HostSessionService(IConfiguration config, ILogger<HostSessionService> logger)
    {
        _hostUser = config.GetValue<string>("HostUser")
            ?? Environment.GetEnvironmentVariable("HOST_USER")
            ?? "root";
        _logger = logger;
    }

    public async Task<List<HostSession>> ListAllSessionsAsync()
    {
        var sessions = new List<HostSession>();

        var tmuxTask = ListTmuxSessionsAsync();
        var ptyTask = ListActiveTerminalsAsync();

        await Task.WhenAll(tmuxTask, ptyTask);

        sessions.AddRange(tmuxTask.Result);
        sessions.AddRange(ptyTask.Result);

        return sessions;
    }

    private async Task<List<HostSession>> ListTmuxSessionsAsync()
    {
        var sessions = new List<HostSession>();
        try
        {
            var output = await RunOnHost(
                "tmux list-sessions -F '#{session_name}:#{session_windows}:#{session_attached}' 2>/dev/null");
            if (output == null) return sessions;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length >= 3)
                {
                    var name = parts[0];
                    var windows = parts[1];
                    var attached = parts[2] == "1" ? "attached" : "detached";
                    sessions.Add(new HostSession(
                        HostSessionType.Tmux,
                        $"tmux:{name}",
                        $"tmux: {name}",
                        $"{windows} windows, {attached}"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list tmux sessions");
        }
        return sessions;
    }

    private async Task<List<HostSession>> ListActiveTerminalsAsync()
    {
        var sessions = new List<HostSession>();
        try
        {
            var script = """
                for pts_dir in /proc/[0-9]*/fd/0; do
                    pid=$(echo "$pts_dir" | cut -d/ -f3)
                    owner=$(stat -c '%U' "/proc/$pid" 2>/dev/null) || continue
                    [ "$owner" = "$USER" ] || continue
                    tty=$(readlink "/proc/$pid/fd/0" 2>/dev/null) || continue
                    echo "$tty" | grep -q '^/dev/pts/' || continue
                    pts=${tty#/dev/pts/}
                    cmdline=$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null)
                    [ -n "$cmdline" ] || continue
                    cwd=$(readlink "/proc/$pid/cwd" 2>/dev/null) || continue
                    comm=$(cat "/proc/$pid/comm" 2>/dev/null) || continue
                    echo "PTS=$pts|PID=$pid|COMM=$comm|CWD=$cwd|CMD=$cmdline"
                done 2>/dev/null | sort -t'|' -k1,1 -u -k2,2n
                """;

            var output = await RunScriptOnHost(script);
            if (output == null) return sessions;

            var ptsSessions = new Dictionary<string, List<(string Pid, string Comm, string Cwd, string Cmd)>>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = new Dictionary<string, string>();
                foreach (var pair in line.Split('|'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2) fields[kv[0]] = kv[1];
                }

                if (!fields.TryGetValue("PTS", out var pts)) continue;
                if (!fields.TryGetValue("PID", out var pid)) continue;
                if (!fields.TryGetValue("COMM", out var comm)) continue;
                if (!fields.TryGetValue("CWD", out var cwd)) continue;
                if (!fields.TryGetValue("CMD", out var cmd)) continue;

                if (!ptsSessions.ContainsKey(pts))
                    ptsSessions[pts] = [];
                ptsSessions[pts].Add((pid, comm, cwd, cmd));
            }

            foreach (var (pts, procs) in ptsSessions.OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 999))
            {
                var interesting = procs
                    .Where(p => !IsBoringShell(p.Comm))
                    .LastOrDefault();

                if (interesting == default)
                    interesting = procs.Last();

                var shortCwd = ShortenPath(interesting.Cwd);
                var dirName = Path.GetFileName(interesting.Cwd.TrimEnd('/'));
                if (string.IsNullOrEmpty(dirName)) dirName = "/";
                var label = $"pts/{pts}: {interesting.Comm} @ {dirName}";
                var description = $"pid {interesting.Pid} | {shortCwd}";

                if (!IsBoringShell(interesting.Comm))
                {
                    var cmdPreview = interesting.Cmd.Length > 60
                        ? interesting.Cmd[..57] + "..."
                        : interesting.Cmd;
                    description = $"{cmdPreview} | {shortCwd}";
                }

                sessions.Add(new HostSession(
                    HostSessionType.ActiveTerminal,
                    $"pty:{pts}:{interesting.Cwd}",
                    label,
                    description));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list active terminals");
        }
        return sessions;
    }

    /// <summary>
    /// Run a simple one-liner on the host via su -c
    /// </summary>
    private async Task<string?> RunOnHost(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nsenter",
            ArgumentList = { "-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
                             "su", "-", _hostUser, "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    /// <summary>
    /// Run a multi-line script on the host by piping it via stdin to bash
    /// </summary>
    private async Task<string?> RunScriptOnHost(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nsenter",
            ArgumentList = { "-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
                             "su", "-", _hostUser, "-c", "bash -s" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static bool IsBoringShell(string comm)
    {
        return comm is "bash" or "zsh" or "sh" or "fish" or "dash" or "login" or "su";
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/home";
        if (path.StartsWith(home))
            return "~" + path[home.Length..];
        return path;
    }
}
