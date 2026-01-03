using Microsoft.Win32;

namespace MonitorWindowsRestore;

public class MonitorWatcher
{
    private readonly Config _config;
    private readonly System.Timers.Timer _debounceTimer;
    private int _lastMonitorCount;

    public event Action? OnMonitorsRestored;
    public event Action<string>? OnLog;

    public MonitorWatcher(Config config)
    {
        _config = config;
        _lastMonitorCount = Screen.AllScreens.Length;

        _debounceTimer = new System.Timers.Timer(_config.RestoreDelayMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (_, _) => CheckAndRestore();
    }

    public void Start()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        OnLog?.Invoke($"Monitor watcher started (currently {_lastMonitorCount} monitors)");
    }

    public void Stop()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounceTimer.Stop();
        OnLog?.Invoke("Monitor watcher stopped");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        OnLog?.Invoke("Display settings changed, debouncing...");
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void CheckAndRestore()
    {
        int currentCount = Screen.AllScreens.Length;
        OnLog?.Invoke($"Monitor count: {_lastMonitorCount} -> {currentCount}");

        // Only restore when going from fewer monitors to 2+ monitors
        if (_lastMonitorCount < 2 && currentCount >= 2)
        {
            OnLog?.Invoke("Both monitors detected, triggering restore");
            OnMonitorsRestored?.Invoke();
        }

        _lastMonitorCount = currentCount;
    }
}
