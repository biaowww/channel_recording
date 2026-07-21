using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ChannelRecording;

/// <summary>用 Windows Media Foundation（经 NAudio 封装）把 WAV 转成 AAC(.m4a)。零外部工具依赖。</summary>
internal static class AudioEncoder
{
    /// <summary>WAV → AAC(.m4a)。失败抛异常，由调用方兜底保留 WAV。</summary>
    public static void WavToAac(string wavPath, string m4aPath, int bitrate = 96000)
    {
        MediaFoundationApi.Startup();
        using var reader = new WaveFileReader(wavPath);
        MediaFoundationEncoder.EncodeToAac(reader, m4aPath, bitrate);
    }
}
