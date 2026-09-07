# Windows 测试包使用说明

这是可联调的第一版，未经过 Windows/打印机现场验收。

1. 解压运行包，修改 `app/appsettings.json` 的 StudioPath 和 ComputerId。
2. 创建独立标准账户，例如 PrintOperator，登录一次配置 Studio 和打印机，然后完全注销。
3. 在另一管理员账户的 64 位 Windows PowerShell 中执行：

```powershell
.\scripts\Install-Recorder.ps1 -PackagePath .\app
.\scripts\Install-Workstation.ps1 -KioskUser PrintOperator -PackagePath .\app
```

4. 登录专用账户测试。认证凭据由本人在本机输入。
5. 按 WINDOWS-DEPLOYMENT.md 完成白名单和实机验收。开机自动登录需管理员另行配置。
6. 回退时注销专用账户，在管理员账户执行 Restore-Workstation.ps1。手动应用的 AppLocker 另行恢复。

日志：`C:\ProgramData\PrintGate\Data\audit.db`。默认鼠标不移动 300 秒后锁定，键盘输入不重置计时；保留工程仅原操作人能继续。

录屏版本首次部署前需运行 Install-Recorder.ps1 安装编码器，设置专用录像目录，并按 RECORDING.md 验证自动清理及采集行为。

管理员查看记录：切换到独立 Windows 管理员账户，运行 `scripts/Start-Admin.ps1` 并确认 UAC。默认打开已安装在 `C:\Program Files\PrintGate` 的程序。若只想用解压包查看本机记录，运行 `scripts/Start-Admin.ps1 -AppPath .\app\PrintGate.exe`。详情见 ADMIN.md。
