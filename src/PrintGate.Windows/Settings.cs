using System.Text.Json;

namespace PrintGate.Windows;

internal sealed record Settings
{
    public string StudioPath { get; init; } = "";
    public string ComputerId { get; init; } = "";
    public string[] AdministratorStudentIds { get; init; } = [];
    public int IdleSeconds { get; init; } = 300;
    public string CasBaseUrl { get; init; } = "";
    public string CasService { get; init; } = "";
    public int RequestTimeoutSeconds { get; init; } = 15;
    public string RecordingsDirectory { get; init; } = @"C:\ProgramData\PrintGate\Recordings";
    public string FfmpegPath { get; init; } = @"Tools\ffmpeg.exe";
    public int RecordingRetentionDays { get; init; } = 7;
    public int RecordingMinimumFreeSpaceMb { get; init; } = 1024;
    public string RecorderExecutable => Path.GetFullPath(FfmpegPath, AppContext.BaseDirectory);

    public static Settings Load()
    {
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
            ?? throw new InvalidOperationException("配置缺失。");
        if (!Path.IsPathFullyQualified(settings.StudioPath) || !File.Exists(settings.StudioPath))
            throw new InvalidOperationException("请管理员在 appsettings.json 中设置正确的 Bambu Studio 完整路径。");
        if (string.IsNullOrWhiteSpace(settings.ComputerId) || settings.IdleSeconds is < 30 or > 3600
            || settings.RequestTimeoutSeconds is < 3 or > 60)
            throw new InvalidOperationException("电脑编号或超时时间配置无效。");
        if (!Uri.TryCreate(settings.CasBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !settings.CasBaseUrl.EndsWith('/') || !Uri.TryCreate(settings.CasService, UriKind.Absolute, out var service)
            || service.Scheme != "https") throw new InvalidOperationException("认证地址必须为 HTTPS，CasBaseUrl 必须以 / 结尾。");
        if (!Path.IsPathFullyQualified(settings.RecordingsDirectory) || settings.RecordingsDirectory.StartsWith(@"\\")
            || settings.RecordingRetentionDays != 7 || settings.RecordingMinimumFreeSpaceMb < 256)
            throw new InvalidOperationException("录像须保存到本地磁盘的绝对路径，保留 7 天，空间预留至少 256 MB。");
        if (!File.Exists(settings.RecorderExecutable))
            throw new InvalidOperationException("缺少录屏组件，请管理员按 RECORDING.md 安装 FFmpeg。");
        return settings;
    }
}
