using System.Collections.Concurrent;
using System.Text;

namespace MonitorWindowsRestore;

/// <summary>
/// Resolves the executable name owning a window.
/// <para>
/// Process.GetProcessById enumerates every process on the system, which is far too
/// expensive for the event hook path - EVENT_OBJECT_LOCATIONCHANGE fires at frame rate
/// for every titled window while one is being dragged. This calls
/// QueryFullProcessImageName directly and caches the answer per window handle, so the
/// hot path is a dictionary lookup.
/// </para>
/// </summary>
public static class ProcessNames
{
    private const int MaxEntries = 1024;
    private const int MaxPathChars = 1024;

    // A window never changes owner, so the handle is a safe cache key. The cached pid is
    // re-checked on every lookup so a recycled handle can't hand back a stale name.
    private static readonly ConcurrentDictionary<IntPtr, (uint Pid, string Name)> Cache = new();

    /// <summary>
    /// Returns the owning executable name (e.g. "chrome.exe"), or null if it cannot be
    /// determined - the process has exited, or it's a protected process we can't query.
    /// </summary>
    public static string? ForWindow(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0) return null;

        if (Cache.TryGetValue(hWnd, out var cached) && cached.Pid == processId)
            return cached.Name;

        var name = Resolve(processId);
        if (name == null) return null;

        // Windows close over time and we never hear about it, so bound the cache rather
        // than tracking liveness - a full rebuild costs one query per live window.
        if (Cache.Count >= MaxEntries) Cache.Clear();
        Cache[hWnd] = (processId, name);

        return name;
    }

    private static string? Resolve(uint processId)
    {
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(MaxPathChars);
            uint size = (uint)buffer.Capacity;

            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;

            return Path.GetFileName(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
