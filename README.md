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
- Uses `SystemEvents.DisplaySettingsChanged` to detect monitor changes
- Skips minimized and unresponsive windows
- Identifies windows by process name + window title hash (handles multiple windows per app)
- Preserves Z-order (window stacking) when restoring
- Only tracks/restores when required monitors are connected

## Requirements

- Windows 10/11
- .NET 10.0 or later
