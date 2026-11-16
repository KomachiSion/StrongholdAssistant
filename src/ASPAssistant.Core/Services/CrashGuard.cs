namespace ASPAssistant.Core.Services;

/// <summary>
/// 原生代码崩溃时托管层拦不住、日志也留不下（v1.2.5）。用标记文件判断上次是不是在用窗口捕获时异常退出：
/// 连续两次就在这台电脑上改用 BitBlt；WebView2 的 GPU 进程崩过一次，以后界面就关掉硬件加速。
/// </summary>
public static class CrashGuard
{
    private const int WgcCrashLimit = 2;
    private static string? _directory;
    private static bool _wgcMarked;
    public static bool WgcDisabled { get; private set; }
    public static bool GpuDisabled { get; private set; }

    private static string PathOf(string name) => Path.Combine(_directory!, name);

    public static void Initialize(string directory)
    {
        _directory = Path.Combine(directory, "crash-guard");
        Directory.CreateDirectory(_directory);
        _wgcMarked = false;
        if (File.Exists(PathOf("wgc-active")))
        {
            File.Delete(PathOf("wgc-active"));
            int count = (int.TryParse(ReadOrEmpty("wgc-crashes"), out var n) ? n : 0) + 1;
            File.WriteAllText(PathOf("wgc-crashes"), count.ToString());
            AppLogger.Warn("CrashGuard", $"上次运行在用窗口捕获时异常退出（连续第 {count} 次）");
            if (count >= WgcCrashLimit) File.WriteAllText(PathOf("wgc-disabled"), DateTimeOffset.Now.ToString("O"));
        }
        WgcDisabled = File.Exists(PathOf("wgc-disabled"));
        GpuDisabled = File.Exists(PathOf("gpu-disabled"));
        if (WgcDisabled) AppLogger.Warn("CrashGuard", "这台电脑上窗口捕获连续异常退出，截图改用 BitBlt");
        if (GpuDisabled) AppLogger.Warn("CrashGuard", "界面的 GPU 进程崩溃过，已关闭界面硬件加速");
    }

    /// <summary>开始用窗口捕获前调用；正常退出时清掉标记。</summary>
    public static void WgcStarting()
    {
        if (_directory is null || _wgcMarked) return;
        File.WriteAllText(PathOf("wgc-active"), DateTimeOffset.Now.ToString("O"));
        _wgcMarked = true;
    }

    public static void GpuCrashed()
    {
        if (_directory is null || GpuDisabled) return;
        File.WriteAllText(PathOf("gpu-disabled"), DateTimeOffset.Now.ToString("O"));
    }

    public static void NormalExit()
    {
        if (_directory is null || !_wgcMarked) return;
        File.Delete(PathOf("wgc-active"));
        File.Delete(PathOf("wgc-crashes"));
        _wgcMarked = false;
    }

    private static string ReadOrEmpty(string name) => File.Exists(PathOf(name)) ? File.ReadAllText(PathOf(name)) : "";
}
