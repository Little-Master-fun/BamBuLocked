# 录屏、保存目录与自动清理

当前代码已接入录屏，Windows 实际采集和播放仍需现场验证。录屏为显式告知的打印操作记录，不包含麦克风/系统音频。

## 准备录屏组件

运行包不内置 FFmpeg。在 Windows 解压后、部署工作站前执行：

```powershell
.\scripts\Install-Recorder.ps1 -PackagePath .\app
.\scripts\Install-Workstation.ps1 -KioskUser PrintOperator -PackagePath .\app
```

源码自行发布时，将 `-PackagePath` 改为 `artifacts\win-x64`。

Install-Recorder 从 FFmpeg 下载页链接的 Gyan Windows builds 获取 release essentials，核对提供方 SHA-256，再放到 `Tools\ffmpeg.exe`，保留随包许可证/来源信息。也可由管理员使用已有的兼容 FFmpeg，在 `FfmpegPath` 配置绝对路径。必须支持 gdigrab、libx264 和 Matroska。

相关文档：[FFmpeg 下载页](https://ffmpeg.org/download.html)、[Windows builds](https://www.gyan.dev/ffmpeg/builds/)、[gdigrab](https://ffmpeg.org/ffmpeg-devices.html#gdigrab)。不关闭 HTTPS 校验。

## 保存与关联

默认配置：

```json
{
  "RecordingsDirectory": "C:\\ProgramData\\PrintGate\\Recordings",
  "FfmpegPath": "Tools\\ffmpeg.exe",
  "RecordingRetentionDays": 7,
  "RecordingMinimumFreeSpaceMb": 1024
}
```

`RecordingsDirectory` 可改为本地专用文件夹，例如 `D:\PrintGate\Recordings`。目前不支持 UNC 共享目录。更换路径时，旧目录不再自动清理，应由管理员迁移旧录像和 sidecar 元数据或另行处理。

每段录像的三个文件：

- `pg-<录像ID>.mkv`：10 fps、H.264、无音频的桌面录像。
- `pg-<录像ID>.json`：会话编号、开始/结束时间、状态；用 SessionId 对照 audit.db 的 sessions 表取得姓名、学号、组织。
- `pg-<录像ID>.lease`：录制期间的排他文件锁，保护当前文件不被清理。

文件名不含姓名或学号。MKV 容器适合尽可能保留中断前已经写入的片段，但异常中断的视频仍可能不完整或无法播放，须实机验证。

## 自动清理规则

1. 程序启动、录屏开始前及运行期间每 30 秒检查。
2. 已结束录像按结束时间保留 7 天，超过期限自动删除。
3. 可用空间低于默认 1024 MB 时，按结束时间从早到晚删除本程序的已结束录像，每删除一段重新检查空间，足够便停止。空间压力下录像可能不足 7 天即被删除，这是用户指定的优先清理规则。
4. 当前录制持有排他 lease，不参与删除。其他正在被占用、无有效元数据或非本程序命名的文件不删除。
5. 可删除录像全部清理后仍不足，录屏不启动或停止，工作站回到认证状态并提示管理员；不删除当前录像来维持运行。
6. 崩溃残留文件仅在 lease 可取得且视频可独占打开时标记 interrupted。结束时间记为恢复时间，不能当作准确崩溃时间。

单个录像可能包含一个很长的使用会话。当前未实现分段循环录制；若磁盘中只剩当前正在录制的大文件，会停止会话而不是删除这个活动文件。

## 录制与锁定顺序

认证前显示录屏提示。认证通过后切入独立打印桌面，启动编码器，收到首帧进度后启动 Studio；如果已有原用户的保留工程，则在原桌面恢复录制。

关闭 Studio、鼠标超时、Windows 会话锁定或故障时：立即清空并禁用认证输入，切回认证桌面，发送 q 正常结束编码器；超过 5 秒未退出则尝试终止编码进程树。确认编码器退出后才重新允许输入认证信息。切换时可能记录极少量无凭据的认证页过渡画面，不应录入下一位用户的账号密码。此顺序需在实机检查，不能仅凭编译保证屏幕采集的桌面隔离效果。

编码器在恢复执行前加入 Windows Job，控制器进程退出会关闭 Job 并终止编码器，防止无人管理地继续录制。守护程序和 AppLocker 的原有边界不变。

## 部署与验收

- 安装脚本仅向专用录像目录授予工作站账户写入权；路径已包含无关文件时拒绝自动改权限。
- AppLocker EXE 审计列表已加入默认 `Tools\ffmpeg.exe`；自定义路径要调整规则。
- 缺少组件、无法采集首帧、编码器退出、持续无进度、空间不足或录像存储错误都不能继续正常开放工作站。
- 验证登录/关闭/超时各产生对应视频；播放确认鼠标、Studio 对话框和实际打印操作可见。
- 验证再次认证前已停止采集；确认登录密码、Windows 安全桌面未录入。
- 验证空间不足时最早视频先删、活动视频不删；使用测试盘/测试配额，不要填满实际系统盘。
- 验证控制器被终止后 FFmpeg 同时退出、中断视频恢复、7 天清理、删除失败的处理，以及多显示器和休眠恢复。
- 当前录像和日志由普通工作站账户写入，不是抗篡改审计存储。视频仅保存在配置的本地目录，不自动上传。
