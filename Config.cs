using System.Text.Json;

namespace MonitorWindowsRestore;

public class Config
{
    public List<string> Programs { get; set; } = [];
    public int DebounceDelayMs { get; set; } = 500;
    public int RestoreDelayMs { get; set; } = 1000;
    public int RequiredMonitorCount { get; set; } = 2;

    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Config Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var defaultConfig = new Config
            {
                Programs =
                [
                    "chrome.exe",
                    "zen.exe",
                    "Obsidian.exe",
                    "thunderbird.exe"
                ]
            };
            defaultConfig.Save();
            return defaultConfig;
        }

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<Config>(json, JsonOptions) ?? new Config();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
