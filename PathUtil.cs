using System.Text;

namespace ChannelRecording;

internal static class PathUtil
{
    /// <summary>
    /// 找项目根目录：从 exe 所在目录向上找含 ChannelRecorder.csproj 的目录；
    /// 找不到（比如发布成独立 exe）就退回到 exe 所在目录。
    /// </summary>
    public static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChannelRecorder.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>录音归档根目录：&lt;项目根&gt;\recording（不存在则创建）。</summary>
    public static string EnsureRecordingDir(string overrideDir = null)
    {
        string dir = overrideDir ?? Path.Combine(FindProjectRoot(), "recording");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把会议名/标题清洗成安全的文件名片段，并限长。</summary>
    public static string SanitizeName(string raw, int maxLen = 24)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "recording";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c is '\r' or '\n' or '\t') sb.Append('_');
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
            else sb.Append(c);
        }

        // 折叠多余空白
        string s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        s = s.Trim(' ', '.', '_');
        if (s.Length == 0) return "recording";
        if (s.Length > maxLen) s = s[..maxLen].TrimEnd(' ', '.', '_');
        return s;
    }

    /// <summary>会话基名：&lt;会议名&gt;_yyyyMMdd_HHmmss（精确到秒，避免同分钟覆盖）。</summary>
    public static string SessionBaseName(string meetingName)
        => $"{SanitizeName(meetingName)}_{DateTime.Now:yyyyMMdd_HHmmss}";
}
