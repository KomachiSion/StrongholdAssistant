using ASPAssistant.Core.Interop;
using MaaFramework.Binding;

namespace ASPAssistant.Core.Services;

/// <summary>物资页专用后台截屏、排序切换及横向拖动；不激活窗口、不点击卡片。</summary>
public sealed class RosterGameControl : IDisposable
{
    private readonly GameConnectionService _connection;
    private readonly GameConnectionState _start;
    private readonly MaaWin32Controller? _controller;
    public RosterGameControl(GameConnectionService connection)
    {
        _connection = connection; _start = connection.State;
        if (_start.Window == 0 || !User32.IsWindow(_start.Window)) throw new IOException("未找到游戏窗口。");
        if (User32.IsIconic(_start.Window)) throw new IOException("游戏窗口已最小化，请先恢复窗口。");
        if (!connection.IsAdb)
        {
            _controller = new(new DesktopWindowInfo(_start.Window, "", "", Win32ScreencapMethod.None,
                Win32InputMethod.SendMessageWithCursorPos, Win32InputMethod.SendMessageWithCursorPos), LinkOption.None, CheckStatusOption.None);
            Check(_controller.LinkStart().Wait());
        }
    }
    public bool Lost => _connection.State.Window != _start.Window || !User32.IsWindow(_start.Window) || User32.IsIconic(_start.Window);
    public byte[] Capture()
    {
        if (Lost) throw new IOException("游戏窗口丢失或已最小化。");
        if (_controller is null) return _connection.CaptureAdbScreen(1920) ?? throw new IOException("模拟器截图失败。");
        // 物资页允许静止画面，沿用原有读取行为；原生截图控制器与扫描共用。
        using var image = _connection.WindowCapture.Capture(_start.Window, requireFresh: false)
            ?? throw new IOException("物资调配页截图失败。");
        using var stream = new MemoryStream();
        image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
    public async Task SelectTierSortAsync(RosterSort sort, CancellationToken token)
    {
        if (Lost) throw new IOException("游戏窗口丢失或已最小化。");
        token.ThrowIfCancellationRequested();
        if (_controller is null) await Task.Run(() => _connection.SelectRosterTierAdb(sort.X, sort.Y), token);
        else
        {
            var rect = User32.GetClientRectScreen(_start.Window) ?? throw new IOException("无法读取游戏客户区。");
            Check(_controller.Click(sort.X * rect.Height / 1080, sort.Y * rect.Height / 1080).Wait());
        }
        await Task.Delay(1000, token);
    }

    public async Task DragAsync(bool left, int distance, CancellationToken token)
    {
        if (Lost) throw new IOException("游戏窗口丢失或已最小化。");
        token.ThrowIfCancellationRequested();
        if (_controller is null) await Task.Run(() => _connection.DragRosterAdb(left, distance), token);
        else
        {
            var rect = User32.GetClientRectScreen(_start.Window) ?? throw new IOException("无法读取游戏客户区。");
            int x1 = (left ? 210 + distance : 210) * rect.Height / 1080;
            int x2 = (left ? 210 : 210 + distance) * rect.Height / 1080;
            int y = 450 * rect.Height / 1080;
            Check(_controller.TouchDown(0, x1, y, 0).Wait());
            try
            {
                for (int i = 1; i <= 50; i++)
                {
                    await Task.Delay(16, token);
                    Check(_controller.TouchMove(0, x1 + (x2 - x1) * i / 50, y, 0).Wait());
                }
                await Task.Delay(300, token);
            }
            finally { Check(_controller.TouchUp(0).Wait()); }
        }
        await Task.Delay(1000, token);
    }
    private static void Check(MaaJobStatus status)
    { if (status != MaaJobStatus.Succeeded) throw new IOException($"物资页控制器失败：{status}"); }
    public void Dispose() => _controller?.Dispose();
}
