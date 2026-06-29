namespace ChannelRecording;

/// <summary>线程安全字节环形缓冲。满了丢最旧（生产者比消费者快时有界，不会无限增长）。</summary>
internal sealed class ByteRing
{
    private readonly byte[] _buf;
    private int _head, _count;
    private readonly object _gate = new();

    public ByteRing(int capacity) => _buf = new byte[Math.Max(1, capacity)];

    public void Write(byte[] src, int offset, int n)
    {
        if (n <= 0) return;
        lock (_gate)
        {
            // 单次写超过容量：只保留最后 capacity 个字节
            if (n > _buf.Length) { offset += n - _buf.Length; n = _buf.Length; }

            int free = _buf.Length - _count;
            if (n > free) { int drop = n - free; _head = (_head + drop) % _buf.Length; _count -= drop; }

            int tail = (_head + _count) % _buf.Length;
            int first = Math.Min(n, _buf.Length - tail);
            Array.Copy(src, offset, _buf, tail, first);
            if (n > first) Array.Copy(src, offset + first, _buf, 0, n - first);
            _count += n;
        }
    }

    /// <summary>清空缓冲（丢弃所有积压）。</summary>
    public void Clear()
    {
        lock (_gate) { _head = 0; _count = 0; }
    }

    /// <summary>读出最多 n 字节到 dst，返回实际读出的字节数。</summary>
    public int Read(byte[] dst, int n)
    {
        lock (_gate)
        {
            int k = Math.Min(n, _count);
            int first = Math.Min(k, _buf.Length - _head);
            Array.Copy(_buf, _head, dst, 0, first);
            if (k > first) Array.Copy(_buf, 0, dst, first, k - first);
            _head = (_head + k) % _buf.Length;
            _count -= k;
            return k;
        }
    }
}
