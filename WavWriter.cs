namespace ChannelRecording;

/// <summary>极简 16-bit PCM WAV 写入器：先写占位头，关闭时回填 RIFF/data 大小。</summary>
internal sealed class WavWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryWriter _bw;
    private long _dataBytes;
    private bool _disposed;

    public WavWriter(string path, ushort channels, uint sampleRate, ushort bitsPerSample)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _bw = new BinaryWriter(_fs);

        ushort blockAlign = (ushort)(channels * bitsPerSample / 8);
        uint byteRate = sampleRate * blockAlign;

        // RIFF header（大小先占位，Dispose 时回填）
        _bw.Write("RIFF"u8);
        _bw.Write(0);                       // [4]  RIFF chunk size
        _bw.Write("WAVE"u8);
        // fmt chunk
        _bw.Write("fmt "u8);
        _bw.Write(16);                      // PCM fmt 块大小
        _bw.Write((ushort)1);               // AudioFormat = PCM
        _bw.Write(channels);
        _bw.Write(sampleRate);
        _bw.Write(byteRate);
        _bw.Write(blockAlign);
        _bw.Write(bitsPerSample);
        // data chunk
        _bw.Write("data"u8);
        _bw.Write(0);                       // [40] data chunk size
    }

    public void Write(byte[] buffer, int count)
    {
        _bw.Write(buffer, 0, count);
        _dataBytes += count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bw.Flush();
        _fs.Seek(4, SeekOrigin.Begin);
        _bw.Write((uint)(36 + _dataBytes));     // RIFF size = 36 + data
        _fs.Seek(40, SeekOrigin.Begin);
        _bw.Write((uint)_dataBytes);            // data size
        _bw.Flush();
        _bw.Dispose();
        _fs.Dispose();
    }
}
