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
    private string _connectionId = Guid.NewGuid().ToString("N");
    private List<HostSession> _hostSessions = [];

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
            if (_sessionId != null)
            {
                PtyService.DisposeSession(_connectionId);
                _sessionId = null;
                _connectionId = Guid.NewGuid().ToString("N");
            }

            var cols = await _terminal.GetColumns();
            var rows = await _terminal.GetRows();

            await _terminal.Clear();

            _sessionId = PtyService.CreateSession(
                _connectionId,
                _selectedTarget,
                cols,
                rows,
                async output =>
                {
                    try
                    {
                        await InvokeAsync(async () =>
                        {
                            await _terminal.Write(output);
                        });
                    }
                    catch (ObjectDisposedException) { }
                });
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
            PtyService.DisposeSession(_connectionId);
        }
    }

    private record TerminalDimensions(int Cols, int Rows);
}
