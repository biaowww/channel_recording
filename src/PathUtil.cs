using System.Text;

namespace ChannelRecording;

internal static class PathUtil
{
    /// <summary>
    /// 找项目根目录：从 exe 所在目录向上找含 ChannelRecorder.csproj 的目录；
    /// 找不到（比如发布成独立 exe）就退回到 exe 所在目录。
    /// </summary>
    /// <summary>
    /// 默认归档根目录：
    /// · 开发环境（沿路径能找到 ChannelRecorder.csproj）→ 仓库根\recording（源码在 src/ 时也落仓库根）
    /// · 独立发布的 exe（找不到工程文件，用户可能把它放在任意位置）→ 我的文档\ChannelRecorder
    /// </summary>
    public static string DefaultRecordingRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChannelRecorder.csproj")))
            {
                string root = (string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
                    ? dir.Parent.FullName : dir.FullName;
                return Path.Combine(root, "recording");
            }
            dir = dir.Parent;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChannelRecorder");
    }

    /// <summary>录音归档根目录：&lt;项目根&gt;\recording（不存在则创建）。</summary>
    public static string EnsureRecordingDir(string overrideDir = null)
    {
        string dir = overrideDir ?? DefaultRecordingRoot();
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
