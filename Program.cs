namespace MonitorWindowsRestore;

static class Program
{
    private static NotifyIcon? _trayIcon;
    private static WindowTracker? _tracker;
    private static MonitorWatcher? _watcher;
    private static WindowRestorer? _restorer;
    private static readonly List<string> _logs = [];
    private static readonly object _logLock = new();

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

        var config = Config.Load();
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
                Log($"Error during auto-restore: {ex.Message}");
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

        Log($"Started - tracking {config.Programs.Count} programs");
        Log($"Current monitors: {Screen.AllScreens.Length}");

        Application.Run();
    }

    private static ContextMenuStrip CreateContextMenu(Config config)
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem($"Monitors: {Screen.AllScreens.Length}")
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
            // Restoring drives other processes' windows and can block on a slow one, so
            // keep it off the UI thread - a stalled message pump here would make this app
            // the thing that hangs anything sending it a message.
            var ui = SynchronizationContext.Current;

            Task.Run(() =>
            {
                string result;
                try
                {
                    _restorer?.RestoreAll();
                    result = "Restored window positions";
                }
                catch (Exception ex)
                {
                    Log($"Error during restore: {ex.Message}");
                    result = $"Restore failed: {ex.Message}";
                }

                ui?.Post(_ => ShowBalloon(result), null);
            });
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
            statusItem.Text = $"Monitors: {Screen.AllScreens.Length}";
        };

        return menu;
    }

    private static void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logLock)
        {
            _logs.Add(entry);
            if (_logs.Count > 100) _logs.RemoveAt(0);
        }
        Console.WriteLine(entry);
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
