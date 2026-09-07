# Windows 10 专业版部署与验收

目标是专用、无管理员权限的打印工作站。所有脚本均需先在可恢复的测试机验证。本仓库未自动修改当前 Mac 或任何 Windows 电脑。

## 1. 确认环境

- 用 `winver` 记录具体版本和 build。AppLocker 的版本/更新要求参照 README 中微软链接。
- 使用本地标准账户，例如 `PrintOperator`；管理员账户另行保留。
- 将官方 Bambu Studio 安装在管理员控制的目录。核对实际 exe 名称与 appsettings。
- 确认工作站可访问认证服务；不关闭 TLS 证书校验来绕过网络问题。
- 首次登录专用账户完成 Studio 安装初始化，随后完全注销。部署脚本需要离线加载该用户 hive。
- 若密码包含 `&`、`+`、`=`，应能正常认证；切勿把真实密码写进测试文件。

## 2. 先验证应用流程

运行 Install-Workstation.ps1 后，只启用了用户界面替换和部分用户策略，**尚未启用 AppLocker 强制限制**。此阶段用来联调，不能称为封闭工作站。

脚本通过 `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell` 设置自定义界面，并保存每个修改值的原始状态。它不是企业版 Shell Launcher，也没有全局替换 Winlogon。

安装目录对专用用户只读，日志目录允许写入。不要把 exe 或配置放在专用用户可修改的下载/桌面目录。更新 Studio、插件或客户端后需重新验证路径和策略。

自动 Windows 登录需由管理员单独配置，例如使用微软 Sysinternals Autologon；脚本不保存明文密码。不要与校园密码复用。应用故障进入 Windows 锁屏时，由管理员维护；不提供通用后门密码。

## 3. 生成并审计白名单

在管理员 PowerShell 执行：

```powershell
Get-AppLockerPolicy -Local -Xml | Set-Content C:\ProgramData\PrintGate\DeploymentBackup\applocker-before.xml
.\scripts\New-AppLockerPolicy.ps1 -KioskUser PrintOperator -OutputPath .\PrintGate-AppLocker.audit.xml
```

生成器为 EXE、Script、MSI、DLL 创建 AuditOnly 集合：管理员/SYSTEM 允许；专用用户的 EXE 仅初始列出 PrintGate、Studio、userinit、ctfmon，DLL 初始列出受保护应用目录和部分系统目录。该配置是起点，不是所有 Studio 版本通用的完整策略。

**合并或替换本机策略前查看已有规则**。通用的 Everyone/Windows/Program Files 允许规则可能让专用用户运行系统工具；不能简单合并后就认为已限制。专用本地测试机可以审阅后加载生成策略：

```powershell
Set-Service AppIDSvc -StartupType Automatic
Start-Service AppIDSvc
Set-AppLockerPolicy -XmlPolicy .\PrintGate-AppLocker.audit.xml
```

在事件查看器 AppLocker 日志中检查 Studio 辅助程序、插件、显卡库、运行时的审计事件。仅补充必要的受保护路径；不要为解决问题放开整个用户目录或全部 Windows EXE。

还应单独管理打包应用/UWP、浏览器协议处理器和其他可执行入口；生成器**没有生成 Appx 集合**。Windows 系统策略、已有域策略及默认允许规则必须一起核对。

通过审计后，由管理员将审阅后的策略集合设为 Enabled，再应用并重启验证。仓库不自动切换强制模式，以免未经验证的 DLL/系统依赖规则导致无法登录。保留可登录的管理员账户用于回退。

恢复已应用的 AppLocker：

```powershell
Set-AppLockerPolicy -XmlPolicy C:\ProgramData\PrintGate\DeploymentBackup\applocker-before.xml
```

## 4. Windows 现场验收（全部待执行）

| 测试 | 预期结果 |
|---|---|
| 冷启动、自动登录 | 直接显示认证界面，无可交互 Explorer 桌面间隙 |
| 未认证按 Alt+Tab / Win / Win+R / Ctrl+Shift+Esc / Alt+F4 | 不能进入 Studio、命令行或普通桌面 |
| Ctrl+Alt+Delete | 可保留注销/管理员切换，但不能借此运行任务管理器绕过 |
| 密码错误、断网、证书错误、响应不含姓名 | 不开放 Studio，显示可理解错误，不泄露请求内容 |
| 组织为空/只有空白 | 提示填写组织，不发起认证、不开放 Studio |
| 组织含中文/单引号 | 成功认证后正确保存，重新锁定后清空输入 |
| 升级已有日志数据库 | 历史记录保留，旧组织为空；新会话正常记录组织 |
| 成功认证 | 日志姓名和学号与学校响应相符，Studio 打开 |
| 密码包含特殊字符 | 正确编码，能通过有效认证 |
| 认证请求期间点击维护/Windows 锁屏 | 迟到的成功响应不能重新开放 Studio |
| 关闭 Studio | 回到认证，结束原因 studio_closed |
| 连续 300 秒鼠标位置未变 | 回到认证，结束原因 mouse_idle_timeout |
| 超时前移动鼠标 | 重新计时；键盘输入不重新计时 |
| 超时保留工程，同一人重新认证 | 新使用会话，恢复原工程 |
| 超时保留工程，另一人认证 | 拒绝接管，不泄露原工程 |
| Studio 在锁定期间退出 | 清除保留关系，下一人可正常进入 |
| 文件传输中/打印中超时 | 检查任务实际行为，不得发送取消指令或丢失传输状态 |
| Studio 单实例/子进程交接 | 不错误识别软件关闭、不转移到未受管实例 |
| 强制结束认证进程/模拟 UI 卡死 | 监控请求系统锁屏，普通用户不能继续操作 |
| 强制结束监控进程 | 主程序撤销会话并请求系统锁屏 |
| 只读/损坏日志数据库 | 不继续授权；明确提示管理员维护 |
| 重启/崩溃残留日志 | 原会话标记 recovered_after_interruption，无自动续用 |
| 多显示器、休眠恢复、Windows 锁屏/解锁 | 所有显示器不能短暂暴露未授权 Studio；失败即不得上线 |
| Studio 文件对话框、链接、插件 | 强制白名单下无法启动非批准程序或改写客户端配置 |
| 管理员恢复 | 可回到普通 Windows 界面，原日志保留 |

日志访问、篡改抵抗和全部终止进程的对抗能力若是硬性验收要求，需要增加受保护的 Windows 服务/中央日志服务及更强账户隔离，不能用当前用户态监控替代。

## 5. 恢复普通桌面

在另一管理员账户下，确保专用账户**完全注销**：

```powershell
.\scripts\Restore-Workstation.ps1
```

脚本恢复部署前的用户策略值，不删除程序与日志，不替你恢复手动应用的 AppLocker。若配置了 Windows 自动登录，应单独撤销。备份目录只允许管理员/SYSTEM 访问。

录屏版本首次部署前需运行 Install-Recorder.ps1 安装编码器，设置专用录像目录，并按 RECORDING.md 验证自动清理及采集行为。
