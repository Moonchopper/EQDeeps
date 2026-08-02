namespace EQDeeps.Server;

/// <summary>
/// Lets the HTTP layer reach the shell window without the API surface
/// depending on WinForms: the launcher attaches the window when running in
/// windowed mode, and POST /api/ui/focus (sent by a second exe launch) brings
/// it to the front. Browser-mode and headless runs never attach, so the
/// endpoint 404s and callers fall back to opening a browser tab.
/// </summary>
public sealed class WindowBridge
{
    private volatile Func<bool>? _focus;

    public void Attach(Func<bool> focus) => _focus = focus;

    public bool TryFocus() => _focus?.Invoke() ?? false;
}
