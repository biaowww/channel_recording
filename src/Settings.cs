using System.Text.Json;

namespace ChannelRecording;

/// <summary>用户偏好，存 %APPDATA%\ChannelRecorder\settings.json，关掉重开也记得。</summary>
internal sealed class Settings
{
    /// <summary>录音归档目录；null = 用默认位置。</summary>
    public string RecordingDir { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChannelRecorder", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* 配置坏了就当默认，别影响启动 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不了就算了，不打断录制 */ }
    }
}
