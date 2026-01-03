# Monitor Windows Restore

A Windows system tray application that remembers window positions and restores them when monitors are reconnected.

## The Problem

When you disconnect a monitor (or it turns off), Windows moves all windows to the remaining display. When the monitor comes back, the windows stay crammed on one screen instead of returning to their original positions.

## The Solution

This app:
1. Periodically saves window positions (only when all monitors are connected)
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
  "pollingIntervalSeconds": 60,
  "restoreDelayMs": 1000,
  "requiredMonitorCount": 2
}
```

| Setting | Description |
|---------|-------------|
| `programs` | List of executable names to track (find in Task Manager > Details) |
| `pollingIntervalSeconds` | How often to save positions (default: 60) |
| `restoreDelayMs` | Delay after monitor reconnect before restoring (default: 1000) |
| `requiredMonitorCount` | Number of monitors required to track/restore (default: 2) |

## How It Works

- Uses `SystemEvents.DisplaySettingsChanged` to detect monitor changes
- Polls window positions at the configured interval
- Skips minimized and unresponsive windows
- Identifies windows by process name + window title hash (handles multiple windows per app)
- Only tracks/restores when 2+ monitors are connected

## Requirements

- Windows 10/11
- .NET 10.0 or later
