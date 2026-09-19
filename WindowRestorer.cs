namespace MonitorWindowsRestore;

public class WindowRestorer
{
    private readonly Config _config;
    private readonly WindowState _state;
    private readonly HashSet<string> _processNames;

    public event Action<string>? OnLog;

    public WindowRestorer(Config config, WindowState state)
    {
        _config = config;
        _state = state;
        _processNames = new HashSet<string>(_config.Programs, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Puts every saved window back. Never prunes: a window we can't find right now may be
    /// a browser still starting up after resume, and its position is worth keeping.
    /// </summary>
    public void RestoreAll()
    {
        var snapshot = _state.Snapshot();
        OnLog?.Invoke($"Restoring {snapshot.Count} windows...");

        var current = GetCurrentWindows();
        var claimed = new HashSet<IntPtr>();
        bool rekeyed = false;
        int restored = 0;

        foreach (var (savedHandle, info) in snapshot)
        {
            var hWnd = ResolveHandle(savedHandle, info, current, claimed);
            if (hWnd == IntPtr.Zero)
            {
                OnLog?.Invoke($"Not found (keeping): {info.WindowTitle}");
                continue;
            }

            claimed.Add(hWnd);
            if (hWnd.ToInt64() != savedHandle)
            {
                _state.Move(savedHandle, hWnd);
                rekeyed = true;
            }

            if (RestoreWindow(hWnd, info)) restored++;
        }

        if (rekeyed)
        {
            try { _state.Save(); }
            catch (Exception ex) { OnLog?.Invoke($"Failed to save state: {ex.Message}"); }
        }

        OnLog?.Invoke($"Restore complete ({restored}/{snapshot.Count})");
    }

    /// <summary>
    /// The saved handle is authoritative while the window it named is still alive - titles
    /// change constantly, handles don't. After a reboot the handles are meaningless, so fall
    /// back to matching process + title against an unclaimed live window.
    /// </summary>
    private static IntPtr ResolveHandle(long savedHandle, WindowInfo info,
        List<(IntPtr Handle, string ProcessName, string Title)> current, HashSet<IntPtr> claimed)
    {
        var hWnd = new IntPtr(savedHandle);
        if (NativeMethods.IsWindow(hWnd) && !claimed.Contains(hWnd))
        {
            var owner = ProcessNames.ForWindow(hWnd);
            if (string.Equals(owner, info.ProcessName, StringComparison.OrdinalIgnoreCase))
                return hWnd;
        }

        foreach (var w in current)
        {
            if (claimed.Contains(w.Handle)) continue;
            if (!string.Equals(w.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
            if (w.Title != info.WindowTitle) continue;
            return w.Handle;
        }

        return IntPtr.Zero;
    }

    private bool RestoreWindow(IntPtr hWnd, WindowInfo info)
    {
        // One SetWindowPlacement call carries the normal rect and the show state together, so
        // a maximized window re-maximizes on the monitor containing the saved rect without the
        // un-maximize / move / re-maximize dance, and a minimized window gets its restore
        // position fixed without being popped open. The async flag posts the request to the
        // owning thread, so a hung window can't block us.
        bool minimized = NativeMethods.IsIconic(hWnd);

        var placement = NativeMethods.WINDOWPLACEMENT.Create();
        placement.flags = NativeMethods.WPF_ASYNCWINDOWPLACEMENT;
        placement.rcNormalPosition = new NativeMethods.RECT
        {
            Left = info.X,
            Top = info.Y,
            Right = info.X + info.Width,
            Bottom = info.Y + info.Height
        };

        if (minimized)
        {
            placement.showCmd = NativeMethods.SW_SHOWMINNOACTIVE;
            if (info.IsMaximized) placement.flags |= NativeMethods.WPF_RESTORETOMAXIMIZED;
        }
        else if (info.IsMaximized)
        {
            // No non-activating variant exists for maximize, so this one does take focus
            placement.showCmd = NativeMethods.SW_SHOWMAXIMIZED;
        }
        else
        {
            placement.showCmd = NativeMethods.SW_SHOWNOACTIVATE;
        }

        if (!NativeMethods.SetWindowPlacement(hWnd, ref placement))
        {
            OnLog?.Invoke($"Failed to restore {info.WindowTitle} (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }

        var state = minimized ? "minimized" : info.IsMaximized ? "maximized" : "normal";
        OnLog?.Invoke($"Restored: {info.WindowTitle} to ({info.X}, {info.Y}) {state}");
        return true;
    }

    private List<(IntPtr Handle, string ProcessName, string Title)> GetCurrentWindows()
    {
        var windows = new List<(IntPtr, string, string)>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsAppWindow(hWnd)) return true;

            var processName = ProcessNames.ForWindow(hWnd);
            if (processName == null || !_processNames.Contains(processName)) return true;

            var title = NativeMethods.GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            windows.Add((hWnd, processName, title));
            return true;
        }, IntPtr.Zero);

        return windows;
    }
}
