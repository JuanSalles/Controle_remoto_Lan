using System;
using System.IO;
using System.Text.Json;

internal sealed class AppConfig
{
    public bool ScanOtherDevices { get; set; } = false;

    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Config.json");
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            string json = File.ReadAllText(path);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return config ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
