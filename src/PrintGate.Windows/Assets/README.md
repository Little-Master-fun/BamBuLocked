# 登录页资源来源

以下图片按用户指定，从本地 OnlineSystem 项目复制，保持原文件不变：

- Login_img.png：校园建筑插画。
- Login_icon1.png：用户图标。
- Login_icon2.png：密码图标。
- Online_Logo.png：管理页侧栏标识，来自同一资源目录。

来源目录：`/Users/mact/code/web/sorps/frontend/OnlineSystem/src/assets/img/`。
参考布局：`src/views/login/index.vue`。页面采用 30% 插画 / 70% 表单、#95BEC3 主色、68px 高圆角描边输入框和圆角登录按钮。

图片通过 WPF Resource 编译进程序，运行不依赖原项目路径或网络。所属组织图标为 WPF 几何图形。当前仅移植样式和图片，认证继续使用 PrintGate 的 CAS 流程；不引入参考页的密码记忆或游客入口。

管理页样式参考 `src/views/operation_records/index.vue` 及其 Header、Table 组件和 `src/components/Aside.vue`：浅蓝绿色侧栏、顶部标签、圆角行和交替底色。权限使用 Windows 管理员令牌，不复制参考系统前端权限逻辑。
