# PrintGate · 实名打印工作站

Windows 10 专业版专用打印电脑的第一版实现。山东大学统一认证成功后读取姓名、学号，并由使用人填写组织，开放 Bambu Studio；关闭软件或鼠标连续 300 秒不移动后重新认证。

**当前状态：代码和跨平台核心测试已完成，可交叉编译 Windows x64 独立运行包。尚未在 Windows、真实校园认证或打印机上联调，不是已经验证“无法绕过”的生产版本。**

## 已实现

- C# WPF 中文认证界面，复刻指定 OnlineSystem 登录页的校园插画、浅蓝绿色圆角表单；图片嵌入程序，无需联网加载。
- 首次 CAS REST TGT → ST → serviceValidate 认证后仅缓存本机账号和身份信息；学校认证服务不可用时，已缓存账号可离线进入，不保存密码、密码校验值或票据。
- 从成功响应读取姓名 `USER_NAME` 和账号 `user`，不以用户输入的姓名作为记录依据。
- 严格 HTTPS 校验、表单编码、请求超时、禁止自动重定向、XML 命名空间和成功节点校验；尝试注销 TGT。
- SQLite 使用记录：姓名、学号、组织、工作站、开始/结束时间及原因；事件记录认证、启动、恢复、锁定。
- 认证桌面和 Studio 桌面分离。超时切回认证桌面，不发送停止打印指令、不强杀 Studio。
- 保留工程仅原操作人认证后可继续；其他用户需等原操作人保存关闭 Studio，或管理员处理。
- 登录后录制打印桌面，关闭/超时后结束；录像保留 7 天，空间不足按最早录像顺序清理，活动录像受排他锁保护。详情见 [录屏说明](docs/RECORDING.md)。
- UI 进程心跳监控；进程退出/失去响应时尝试切回认证桌面并持续请求 Windows 锁屏。
- 日志失败时拒绝开放；退出会话先撤销授权，再写结束日志。
- 管理员部署/恢复脚本、自定义用户界面配置、AppLocker 审计策略生成器。
- 管理员可视化页面：使用次数/人数、未结束及异常统计，按身份/组织/日期筛选、会话详情、关联录像、系统事件及 CSV 导出。使用独立 Windows 管理员身份打开，见 [管理页面说明](docs/ADMIN.md)。
- 使用人员按学号汇总频次，点击进入个人全部历史及录像；输入单个日期和具体时刻，即可弹出包含该时刻的使用区间，列表显示起止时间。
- 在 `AdministratorStudentIds` 配置校园管理员学号后，原登录页认证成功即进入记录页面；关闭、鼠标超时或 Windows 锁定后回到认证页。默认空名单不开放校园管理入口。

## 快速构建

安装 .NET 8 SDK 后，在项目根目录运行：

```powershell
dotnet restore PrintGate.sln
dotnet build PrintGate.sln -c Release
dotnet test tests/PrintGate.Tests/PrintGate.Tests.csproj -c Release
.\scripts\Publish.ps1
```

也可在 Mac 上交叉发布：

```sh
dotnet publish src/PrintGate.Windows/PrintGate.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

运行包为 `artifacts/win-x64/`，需复制**整个文件夹**，不能只复制 exe。包自带 .NET 运行时；WPF 界面及 Win32 桌面 API 只能在 Windows 运行。

离线完整包 `PrintGate-win-x64-offline.zip` 已包含 Windows FFmpeg 及其许可证、来源和校验信息。该包无需在目标电脑下载录屏组件；校园登录仍需网络。

## 首次 Windows 测试

1. 保留一个可用的独立管理员账户。创建专用的非管理员本地账户，先登录一次生成用户目录，再注销。
2. 安装官方 Bambu Studio，在专用账户下完成打印机连接和必要组件安装，再关闭软件并注销。
3. 修改运行包的 `appsettings.json`，确认 `StudioPath` 指向实际 exe、`ComputerId` 唯一。
4. 在另一管理员账户下执行下列部署脚本；脚本会替换**指定账户**的 Explorer 入口，不作用于管理员账户：

```powershell
.\scripts\Install-Recorder.ps1 -PackagePath .\artifacts\win-x64
.\scripts\Install-Workstation.ps1 -KioskUser PrintOperator -PackagePath .\artifacts\win-x64
```

5. 登录专用账户进入认证界面。用真实账号在该电脑上测试，不把密码发给开发者。
6. 初期测试完可用 `Ctrl+Alt+Delete` 注销/切换至管理员。应用自身不提供绕过认证进入桌面的按钮。
7. 按 [Windows 部署与验收](docs/WINDOWS-DEPLOYMENT.md) 完成白名单审计及强制策略，才进行无人值守试用。

**开机直接进入认证界面**还需要管理员配置专用 Windows 账户的自动登录；脚本不保存 Windows 密码，也不自动设置 AutoAdminLogon。Windows 自动登录和校园认证是两层独立流程。未配置自动登录时，需要先在 Windows 登录专用账户。

## 配置

自定义管理员用户名和密码：使用新版包中的 `scripts/Set-LocalAdministrator.ps1` 打开设置窗口，详见 [本地管理员说明](docs/LOCAL-ADMIN.md)。登录页选择“本地管理员登录”后验证本机账号，不使用校园密码。

| 配置项 | 含义 |
|---|---|
| `StudioPath` | 管理员安装的官方 Studio exe 绝对路径 |
| `ComputerId` | 本机编号，写入日志 |
| `IdleSeconds` | 默认 300，范围 30–3600 秒，仅检测鼠标位置变化 |
| `CasBaseUrl` | 默认 `https://pass.sdu.edu.cn/cas/`，末尾必须有 `/` |
| `CasService` | 示例中的学校门户服务地址；正式接入需要学校确认 |
| `RecordingsDirectory` | 本地专用录像目录，可改为 D 盘等指定地点 |
| `FfmpegPath` | 编码器路径，默认 Tools\ffmpeg.exe；离线完整包已内置，自行发布需安装 |
| `RecordingRetentionDays` | 7 天；磁盘不足时可提前删除最早录像 |
| `RecordingMinimumFreeSpaceMb` | 默认预留 1024 MB，低于此值开始清理 |
| `RequestTimeoutSeconds` | 每次 HTTP 请求的超时，默认 15 秒 |

接口基于用户提供的 [sduAuth 示例](https://github.com/Little-Master-fun/sduAuth/blob/main/src/ulits/authentication.js) 重新实现，没有执行或复制该仓库的前端应用。示例不是学校正式接入承诺；实际字段、网络访问条件、服务地址有效性待联调。如果 namespace 或结构不同，应使用**脱敏 XML 样例**调整解析器，不要降低到“只要出现姓名就认为成功”。

## 数据保存

默认位置：`C:\ProgramData\PrintGate\Data\audit.db`。

`sessions` 表包含：

- `session_id`：每次认证开放产生一条新会话。
- `student_id`、`name`：服务器返回的账号和姓名，学号保留前导零。
- `organization`：用户自行填写的组织，每次认证必填，去除首尾空白，最长 100 个字符；不属于学校认证的身份属性。旧数据库自动增加此列，历史记录保留为 NULL（未采集）。
- `computer_id`、`started_at`、`ended_at`、`end_reason`。

时间使用 UTC ISO 8601，查询显示时再转换当地时间。崩溃后恢复的结束时间是**恢复时间**，原因 `recovered_after_interruption`，不冒充准确崩溃时间。`events` 表只写受控事件类型，不写 HTTP 请求、密码、票据或原始认证响应。当前为本地使用日志，不含模型文件、耗材、实际打印结果。

管理员通过 `scripts/Start-Admin.ps1` 打开只读管理页面，无需关闭正在运行的工作站程序。程序运行时数据库可能有 `-wal` 文件，备份应使用 SQLite 在线备份或在程序完全退出后复制；不能只复制活动中的主数据库文件。

## 安全边界与未完成的现场验证

- **独立桌面不是独立用户/安全主体**。本程序、监控进程和 Studio 都运行在同一普通账户下；有能力在该账户执行任意代码的人可能绕过控制。
- 因此必须结合受保护的安装目录、自定义界面、禁止任意应用执行的系统策略；全屏、禁用关闭按钮和桌面名称均不作为安全凭据。
- 当前监控为**用户会话进程，不是已实现的高权限 Windows 服务**。它无法防止有权限的人同时终止两个进程。强审计/抗篡改要求还需特权服务或中央日志服务。
- 日志文件对专用账户可写，不能承诺防篡改。管理页面仅提供管理员只读查看和导出，不提供日志编辑。
- `LockWorkStation` 是异步锁屏请求，不代表已验证锁定成功。系统安全桌面、会话切换、多屏、RDP、休眠恢复等必须实机测试。
- 程序不支持带着未知 Studio 进程恢复：启动时发现当前 Windows 会话已有 Studio 会拒绝认证，要求管理员处理/注销。
- 校园认证在客户端执行，不保存密码；托管运行时中的短期字符串无法保证立即从内存抹除。
- 鼠标按每 200ms 位置采样；键盘输入或仅点击/滚轮而鼠标位置不变不会延长会话。位置采样不是防模拟输入机制。
- 当前没有超时前 30 秒提示条；到期直接回到认证界面。
- 不主动取消打印，不等于已验证任意型号/连接方式都不受桌面切换或 Windows 注销影响。
- 不限制手机、其他电脑或打印机本机发起的打印。

## 目录

```text
src/PrintGate.Core/       CAS 认证、身份模型、SQLite 日志和会话规则
src/PrintGate.Windows/    WPF 界面、Win32 桌面、鼠标监控、故障监控
 tests/PrintGate.Tests/   不访问真实学校服务的自动化测试
scripts/                 发布、部署、恢复及白名单生成
 docs/                   Windows 验收步骤和协议约定
```

系统设计依据：[Custom User Interface（支持 Pro）](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-winlogon)、[AppLocker 要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker)、[桌面访问权限](https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights)、[Windows 锁屏 API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-lockworkstation)。
