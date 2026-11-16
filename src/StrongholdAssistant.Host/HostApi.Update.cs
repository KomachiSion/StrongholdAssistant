using ASPAssistant.Core.Services;
using System.Net.Http;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    public UpdateService Updater { get; set; } = new();
    private UpdateInfo? _availableUpdate;
    public bool UpdatePrepared { get; private set; }
    private object? _downloadStatus;
    // 玩家点过「更新」「安装并重启」：下完就装。自动下载的（v1.0.4）下完等玩家点
    private bool _installWhenReady;
    public event Action? UpdateReady;

    private object ReportUpdate(object status)
    {
        Changed?.Invoke("update", status);
        return status;
    }

    public async Task<object> CheckUpdateAsync()
    {
        if (Volatile.Read(ref _downloadStatus) is { } downloading) return downloading;
        if (UpdatePrepared && _availableUpdate is { } prepared) return ReportUpdate(new { state = "ready", version = prepared.Version });
        Updater.GetUpdateProxy = () => Settings.UpdateProxy;
        if (Updater.IsDevBuild) return new { state = "latest" };
        ReportUpdate(new { state = "checking" });
        _availableUpdate = Settings.UpdateSource == "mirrorchyan"
            ? await Updater.CheckMirrorChyanAsync(Settings.MirrorChyanCdk)
            : await Updater.CheckForUpdateAsync();
        if (Updater.LastError is { } message) return ReportUpdate(UpdateFailure(message));
        var status = new Dictionary<string, object> { ["state"] = _availableUpdate is null ? "latest" : "available" };
        if (Updater.CdkExpiresAt is { } expires) status["cdkExpiresAt"] = expires;
        if (_availableUpdate is not { } info) return ReportUpdate(status);
        status["version"] = info.Version;
        status["notesUrl"] = info.NotesUrl;
        status["source"] = info.Source;
        if (info.Notes is { } notes) status["notes"] = notes;
        return ReportUpdate(status);
    }

    /// <summary>玩家点「更新」：没下就下、在下就等，下好了马上安装并重启。</summary>
    public Task ApplyUpdateAsync()
    {
        _installWhenReady = true;
        if (UpdatePrepared) { UpdateReady?.Invoke(); return Task.CompletedTask; }
        return DownloadUpdateAsync();
    }

    /// <summary>后台下载新版本，下好推送 ready，等玩家点安装（v1.0.4）。</summary>
    public Task DownloadUpdateAsync()
    {
        if (_availableUpdate is not { } info || UpdatePrepared) return Task.CompletedTask;
        var status = new { state = "downloading", version = info.Version, progress = 0d };
        if (Interlocked.CompareExchange(ref _downloadStatus, status, null) is not null) return Task.CompletedTask;
        Updater.GetUpdateProxy = () => Settings.UpdateProxy;
        UpdatePrepared = false;
        ReportUpdate(status);
        _ = Task.Run(() => PrepareUpdateAsync(info));
        return Task.CompletedTask;
    }

    public void ClearPreparedUpdate()
    {
        UpdatePrepared = false;
        Updater.ClearPendingUpdate();
    }

    private async Task PrepareUpdateAsync(UpdateInfo info)
    {
        var lastProgress = -0.05;
        try
        {
            if (info.Source == "mirrorchyan")
            {
                var refreshed = await Updater.ReadMirrorChyanAsync(Settings.MirrorChyanCdk);
                if (refreshed is null)
                {
                    ReportUpdate(UpdateFailure(Updater.LastError!));
                    return;
                }
                if (UpdateService.TryParseVersion(refreshed.Version)! <= Updater.CurrentVersion)
                {
                    ReportUpdate(UpdateFailure("Mirror酱 上的版本不比当前新，已取消更新"));
                    return;
                }
                info = refreshed;
            }
            await Updater.DownloadAndApplyUpdateAsync(info, new UpdateProgress(value =>
            {
                if (value < 1 && value - lastProgress < 0.05) return;
                lastProgress = value;
                var status = new { state = "downloading", version = info.Version, progress = value };
                Volatile.Write(ref _downloadStatus, status);
                ReportUpdate(status);
            }));
            UpdatePrepared = true;
            ReportUpdate(new { state = "ready", version = info.Version });
            if (_installWhenReady) UpdateReady?.Invoke();
        }
        catch (Exception ex) when (ex is UpdateIntegrityException or IOException or HttpRequestException or TaskCanceledException
                                   or UnauthorizedAccessException or InvalidDataException)
        {
            var message = UpdateService.DescribeError(ex, Updater.GetUpdateProxy());
            if (info.Source == "mirrorchyan") message = message.Replace("更新源", "Mirror酱");
            ReportUpdate(UpdateFailure(message));
        }
        finally { Volatile.Write(ref _downloadStatus, null); }
    }

    private Dictionary<string, object> UpdateFailure(string message)
    {
        var status = new Dictionary<string, object> { ["state"] = "failed", ["message"] = message };
        if (Updater.CdkProblem) status["cdkProblem"] = true;
        return status;
    }

    private sealed class UpdateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
