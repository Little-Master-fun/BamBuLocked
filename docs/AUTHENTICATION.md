# 认证协议与日志约定

本实现参考 sduAuth 的认证链路，不依赖其开发服务器。

1. POST `{CasBaseUrl}restlet/tickets`，使用 `application/x-www-form-urlencoded` 发送 username/password。
2. 检查成功 HTTP 状态和 TGT 票据格式。
3. POST `{CasBaseUrl}restlet/tickets/{TGT}`，使用 `Content-Type: text/plain`，正文为 `service=` 加配置中的完整原始地址（不对整个地址做表单编码），与已验证的 sduAuth 请求一致，获取 ST。
4. GET `{CasBaseUrl}serviceValidate?ticket={ST}&service={service}`。
5. XML 根节点必须是 CAS serviceResponse，必须有且只有一个 authenticationSuccess，没有 authenticationFailure；提取唯一直接子 user 和唯一 USER_NAME 属性。
6. finally 中尝试 DELETE TGT，清理最多等待 3 秒；失败不泄露票据到日志。学校是否支持注销须现场验证。

支持的 XML 样例（虚构身份）：

```xml
<cas:serviceResponse xmlns:cas="http://www.yale.edu/tp/cas">
  <cas:authenticationSuccess>
    <cas:user>00123456</cas:user>
    <cas:attributes><cas:USER_NAME>测试用户</cas:USER_NAME></cas:attributes>
  </cas:authenticationSuccess>
</cas:serviceResponse>
```

cas/sso 等前缀可以变化，命名空间 URI 必须匹配。收到不同结构时默认拒绝认证；真实响应结构当前未验证。CAS service 地址当前继承示例，正式使用应确认学校服务接入规则。

HTTP 客户端禁用自动重定向及 cookies，不禁用 TLS 校验。每次请求默认 15 秒超时，响应缓冲上限 128 KiB，XML 禁止 DTD/外部实体。取消、网络错误、账号错误、无法解析均不能产生授权会话。

所有认证请求使用 HTTP/1.1，并携带已验证测试代理的 `User-Agent: axios/1.7.9 PrintGateTest/1.0` 和 `Accept: application/json, text/plain, */*`。
WPF 客户端直接通过 HTTPS 请求学校，不受浏览器 CORS 限制，无需启动 HTML 测试页或本地 Node 代理。账号密码仍使用表单编码，避免特殊字符改变字段含义。

首次通过学校认证的普通账号会在该工作站的 Printer 用户配置目录中建立本地认证缓存，只保存输入账号及学校返回的姓名和学号；不保存明文密码、密码校验值、CAS 票据或认证响应。每次登录仍优先请求学校认证服务。仅当连接、超时或服务端错误导致认证服务不可用时，才按输入账号查找本地缓存；找到后直接进入，不校验该次输入的密码。学校明确返回账号或密码错误时不使用本地缓存。

日志只记录服务器确认身份之后的使用会话。认证失败事件不记录密码、输入账号、姓名或 HTTP 异常对象。认证成功但上个工程属于别人，记录 retained_owner_mismatch，并保持锁定。

操作记录是**客户端使用会话**，不是打印作业审计：无法从启动/关闭 Studio 推断实际发送了几个任务，也无法确认耗材和结果。

组织字段 `organization` 在客户端填写，不发送给 CAS。输入去除首尾空白后需为 1–100 个字符且不含控制字符；未填写时不发起认证。认证成功后将其与服务器返回的姓名、学号保存到本次会话，重新锁定时清空输入。组织不参与身份校验或保留工程的所有权判断。旧数据库自动补充可空列，历史数据不推测组织。
