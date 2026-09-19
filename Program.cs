namespace MonitorWindowsRestore;

static class Program
{
    private static NotifyIcon? _trayIcon;
    private static WindowTracker? _tracker;
    private static MonitorWatcher? _watcher;
    private static WindowRestorer? _restorer;
    private static readonly List<string> _logs = [];
    private static readonly object _logLock = new();

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "log.txt");
    private const long MaxLogBytes = 1024 * 1024;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Ensure single instance
        using var mutex = new Mutex(true, "MonitorWindowsRestore_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Monitor Windows Restore is already running.", "Already Running",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var config = Config.Load(out var configError);
        if (configError != null)
        {
            Log(configError);
            MessageBox.Show(configError, "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var state = WindowState.Load();

        _watcher = new MonitorWatcher(config);
        _tracker = new WindowTracker(config, state, _watcher);
        _restorer = new WindowRestorer(config, state);

        // Wire up events
        _tracker.OnLog += Log;
        _restorer.OnLog += Log;
        _watcher.OnLog += Log;
        _watcher.OnMonitorsRestored += () =>
        {
            try
            {
                _restorer.RestoreAll();
            }
            catch (Exception ex)
            {
                Log($"Error during auto-restore: {ex}");
            }
        };

        // Create system tray icon
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        _trayIcon = new NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            Visible = true,
            Text = "Monitor Windows Restore",
            ContextMenuStrip = CreateContextMenu(config)
        };

        // Start services
        _tracker.Start();
        _watcher.Start();

        Log($"Started - tracking {config.Programs.Count} programs, {NativeMethods.MonitorCount()} monitors, " +
            $"{state.Count} saved windows");

        Application.Run();
    }

    private static ContextMenuStrip CreateContextMenu(Config config)
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem($"Monitors: {NativeMethods.MonitorCount()}")
        {
            Enabled = false
        };
        menu.Items.Add(statusItem);

        menu.Items.Add(new ToolStripSeparator());

        var trackNowItem = new ToolStripMenuItem("Track Windows Now");
        trackNowItem.Click += (_, _) =>
        {
            _tracker?.TrackWindows();
            ShowBalloon("Tracked window positions");
        };
        menu.Items.Add(trackNowItem);

        var restoreNowItem = new ToolStripMenuItem("Restore Windows Now");
        restoreNowItem.Click += (_, _) =>
        {
            // Placements are posted asynchronously to each window's owning thread, so this
            // can't block on a hung window and is safe to run on the UI thread.
            try
            {
                _restorer?.RestoreAll();
                ShowBalloon("Restored window positions");
            }
            catch (Exception ex)
            {
                Log($"Error during restore: {ex}");
                ShowBalloon($"Restore failed: {ex.Message}");
            }
        };
        menu.Items.Add(restoreNowItem);

        menu.Items.Add(new ToolStripSeparator());

        var showLogsItem = new ToolStripMenuItem("Show Recent Logs");
        showLogsItem.Click += (_, _) => ShowLogs();
        menu.Items.Add(showLogsItem);

        var openConfigItem = new ToolStripMenuItem("Open Config Folder");
        openConfigItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start("explorer.exe", AppContext.BaseDirectory);
        };
        menu.Items.Add(openConfigItem);

        menu.Items.Add(new ToolStripSeparator());

        var programsMenu = new ToolStripMenuItem("Tracked Programs");
        foreach (var prog in config.Programs)
        {
            programsMenu.DropDownItems.Add(new ToolStripMenuItem(prog) { Enabled = false });
        }
        menu.Items.Add(programsMenu);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _tracker?.Stop();
            _watcher?.Stop();
            _trayIcon?.Dispose();
            Application.Exit();
        };
        menu.Items.Add(exitItem);

        // Update monitor count when menu opens
        menu.Opening += (_, _) =>
        {
            statusItem.Text = $"Monitors: {NativeMethods.MonitorCount()}";
        };

        return menu;
    }

    private static void Log(string message)
    {
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_logLock)
        {
            _logs.Add(entry);
            if (_logs.Count > 100) _logs.RemoveAt(0);

            // Failures happen while nobody is watching the tray, so keep a file too. Roll it
            // over once, so it can't grow without bound.
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                {
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
                }
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down
            }
        }
    }

    private static void ShowLogs()
    {
        string logs;
        lock (_logLock)
        {
            logs = string.Join(Environment.NewLine, _logs.TakeLast(20));
        }

        MessageBox.Show(logs, "Recent Logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ShowBalloon(string message)
    {
        _trayIcon?.ShowBalloonTip(2000, "Monitor Windows Restore", message, ToolTipIcon.Info);
    }
}
