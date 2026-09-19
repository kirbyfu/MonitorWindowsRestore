# Monitor Windows Restore

A Windows system tray application that remembers window positions and restores them when monitors are reconnected.

## The Problem

When you disconnect a monitor (or it turns off), Windows moves all windows to the remaining display. When the monitor comes back, the windows stay crammed on one screen instead of returning to their original positions.

## The Solution

This app:
1. Captures window positions in real-time using Windows event hooks
2. Detects when monitors are reconnected
3. Automatically restores windows to their saved positions

## Usage

```
dotnet run
```

Or build and run the executable directly. The app runs in the system tray.

### Tray Menu Options

- **Track Windows Now** - Manually save current positions
- **Restore Windows Now** - Manually restore to saved positions
- **Show Recent Logs** - View recent activity
- **Open Config Folder** - Access config files
- **Tracked Programs** - View which programs are being tracked

## Configuration

Edit `config.json` in the application directory:

```json
{
  "programs": [
    "chrome.exe",
    "zen.exe",
    "Obsidian.exe",
    "thunderbird.exe"
  ],
  "debounceDelayMs": 500,
  "restoreDelayMs": 1000,
  "requiredMonitorCount": 2
}
```

| Setting | Description |
|---------|-------------|
| `programs` | List of executable names to track (find in Task Manager > Details) |
| `debounceDelayMs` | Delay after window movement before saving position (default: 500) |
| `restoreDelayMs` | Delay after monitor reconnect before restoring (default: 1000) |
| `requiredMonitorCount` | Number of monitors required to track/restore (default: 2) |

## How It Works

- Uses Windows event hooks (`SetWinEventHook`) to capture window movements immediately
- Debounces rapid changes (e.g., during window dragging) before saving
- Captures with `GetWindowPlacement`, so a maximized window's un-maximized size is kept too
- Uses `SystemEvents.DisplaySettingsChanged` to detect monitor changes, freezes capture as soon as a change is seen, and discards any capture already in flight
- Only restores after the monitor count actually dropped below the required number and came back; resolution or DPI changes alone do nothing
- Restores with `SetWindowPlacement` posted asynchronously, so an unresponsive window can't stall the rest
- Leaves minimized windows minimized (their restore position is still corrected)
- Identifies windows by handle while they're alive, so title changes (browser tabs) don't matter; falls back to process name + title after a reboot
- Never deletes a saved position during restore; only the startup/manual scan and window-close events prune
- Declares per-monitor DPI awareness, so positions are in physical pixels on mixed-DPI setups
- Only tracks/restores when required monitors are connected
- Writes a log to `log.txt` next to the executable (rolled over at 1 MB)

## Requirements

- Windows 10/11
- .NET 10.0 or later
