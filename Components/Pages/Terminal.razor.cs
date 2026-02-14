using ClaudeToiletClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XtermBlazor;

namespace ClaudeToiletClient.Components.Pages;

public partial class Terminal : IDisposable
{
    private Xterm _terminal = null!;
    private string? _sessionId;
    private string _selectedTarget = "new-shell";
    private bool _isConnecting;
    private List<HostSession> _hostSessions = [];
    private List<PtySessionInfo> _serverSessions = [];

    private readonly TerminalOptions _terminalOptions = new()
    {
        CursorBlink = true,
        CursorStyle = CursorStyle.Bar,
        FontSize = 14,
        FontFamily = "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
        Theme = new Theme
        {
            Background = "#1a1a2e",
            Foreground = "#e2e2e2",
            Cursor = "#e2e2e2",
            CursorAccent = "#1a1a2e",
            SelectionBackground = "#533483",
        },
    };

    private readonly HashSet<string> _addons = [];
    private DotNetObjectReference<Terminal>? _dotNetRef;

    protected override async Task OnInitializedAsync()
    {
        await LoadSessions();
    }

    private async Task OnFirstRender()
    {
        await Task.Delay(200);
        await FitTerminal();
        _dotNetRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("terminalInterop.setupResizeObserver", "terminal-container",
            _dotNetRef);
        await JS.InvokeVoidAsync("terminalInterop.preventDefaultTouchHandlers", "terminal-container");
        await StartSession();
    }

    private async Task FitTerminal()
    {
        try
        {
            var dims = await JS.InvokeAsync<TerminalDimensions>(
                "terminalInterop.fitTerminal", "terminal-container");
            if (dims.Cols > 0 && dims.Rows > 0)
            {
                await _terminal.Resize(dims.Cols, dims.Rows);
            }
        }
        catch { }
    }

    private async Task StartSession()
    {
        if (_isConnecting) return;
        _isConnecting = true;
        StateHasChanged();

        try
        {
            // Detach from current session (don't kill it)
            if (_sessionId != null)
            {
                PtyService.DetachFromSession(_sessionId);
                _sessionId = null;
            }

            var cols = await _terminal.GetColumns();
            var rows = await _terminal.GetRows();
            await _terminal.Clear();

            if (_selectedTarget.StartsWith("server-session:"))
            {
                // Reconnect to existing server-side session
                var existingSessionId = _selectedTarget["server-session:".Length..];
                var attached = PtyService.AttachToSession(existingSessionId, CreateOutputCallback());

                if (attached != null)
                {
                    _sessionId = attached;
                    PtyService.Resize(_sessionId, cols, rows);

                    // Replay scrollback
                    var scrollback = PtyService.GetScrollback(_sessionId);
                    if (!string.IsNullOrEmpty(scrollback))
                    {
                        await _terminal.Write(scrollback);
                    }
                }
                else
                {
                    await _terminal.WriteLine("\r\nSession no longer available.");
                }
            }
            else
            {
                // Create new session, then attach
                _sessionId = PtyService.CreateSession(_selectedTarget, cols, rows);
                PtyService.AttachToSession(_sessionId, CreateOutputCallback());
            }
        }
        catch (Exception ex)
        {
            await _terminal.WriteLine($"\r\nError: {ex.Message}");
        }
        finally
        {
            _isConnecting = false;
            StateHasChanged();
        }
    }

    private Func<string, Task> CreateOutputCallback()
    {
        return async output =>
        {
            try
            {
                await InvokeAsync(async () =>
                {
                    await _terminal.Write(output);
                });
            }
            catch (ObjectDisposedException) { }
        };
    }

    private async Task OnData(string data)
    {
        if (_sessionId == null) return;
        PtyService.Write(_sessionId, data);
        await Task.CompletedTask;
    }

    private void SendShiftTab()
    {
        if (_sessionId != null) PtyService.Write(_sessionId, "\x1b[Z");
    }

    private void SendCtrlC()
    {
        if (_sessionId != null) PtyService.Write(_sessionId, "\x03");
    }

    private void SendEscape()
    {
        if (_sessionId != null) PtyService.Write(_sessionId, "\x1b");
    }

    private async Task OnSessionChanged()
    {
        await StartSession();
    }

    private async Task RefreshSessions()
    {
        await LoadSessions();
        StateHasChanged();
    }

    private async Task LoadSessions()
    {
        _hostSessions = await SessionService.ListAllSessionsAsync();
        _serverSessions = PtyService.GetActiveSessions()
            .Where(s => s.Id != _sessionId)
            .ToList();
    }

    private async Task CloseCurrentSession()
    {
        if (_sessionId != null)
        {
            PtyService.DestroySession(_sessionId);
            _sessionId = null;
            await _terminal.Clear();
            await _terminal.WriteLine("\r\nSession closed.");
            await LoadSessions();
            _selectedTarget = "new-shell";
            StateHasChanged();
        }
    }

    [JSInvokable]
    public async Task OnContainerResized()
    {
        try
        {
            await FitTerminal();
            if (_sessionId != null)
            {
                var cols = await _terminal.GetColumns();
                var rows = await _terminal.GetRows();
                PtyService.Resize(_sessionId, cols, rows);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _dotNetRef?.Dispose();
        if (_sessionId != null)
        {
            PtyService.DetachFromSession(_sessionId);
        }
    }

    private static string FormatSessionLabel(PtySessionInfo session)
    {
        var target = session.Target switch
        {
            "new-shell" => "shell",
            var t when t.StartsWith("tmux:") => $"tmux:{t[5..]}",
            var t when t.StartsWith("pty:") => $"pty:{t.Split(':')[1]}",
            var t => t
        };
        var age = DateTime.UtcNow - session.CreatedAt;
        var ageStr = age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m" : $"{(int)age.TotalHours}h";
        var status = session.HasClient ? "attached" : "detached";
        return $"[{session.Id[..6]}] {target} ({ageStr}, {status})";
    }

    private record TerminalDimensions(int Cols, int Rows);
}
