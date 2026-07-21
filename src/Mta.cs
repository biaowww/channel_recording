namespace ChannelRecording;

/// <summary>
/// 在 MTA 线程上执行 COM 操作。WASAPI / Core Audio 的激活在 STA 线程上会 E_NOINTERFACE，
/// 而 GUI 线程是 STA，因此凡是会激活音频 COM 的入口都经此跳到 MTA 执行。
/// </summary>
internal static class Mta
{
    public static void Run(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            action();
            return;
        }
        Exception err = null;
        var t = new Thread(() => { try { action(); } catch (Exception e) { err = e; } })
        {
            IsBackground = true,
            Name = "mta-com",
        };
        t.Start();      // 新线程默认 MTA
        t.Join();
        if (err != null) throw err;
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default;
        Run(() => { result = func(); });
        return result;
    }
}
