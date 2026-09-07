using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace PrintGate.Core;

// Mirrors the sample's REST ticket exchange, not an official SDU integration contract.
public sealed class CasAuthenticator(HttpClient client, Uri baseUri, string service) : IAuthenticator
{
    public async Task<Identity> AuthenticateAsync(string account, string password, CancellationToken cancellationToken)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps || !Uri.TryCreate(service, UriKind.Absolute, out var serviceUri)
            || serviceUri.Scheme != Uri.UriSchemeHttps)
            throw new AuthenticationException("认证地址必须使用 HTTPS。");
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            throw new AuthenticationException("请输入学号和密码。");

        string? tgt = null;
        try
        {
            using var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = account.Trim(), ["password"] = password
            });
            tgt = await PostTicket("restlet/tickets", credentials, "TGT-", cancellationToken);
            using var target = new FormUrlEncodedContent(new Dictionary<string, string> { ["service"] = service });
            var st = await PostTicket($"restlet/tickets/{Uri.EscapeDataString(tgt)}", target, "ST-", cancellationToken);
            var path = $"serviceValidate?ticket={Uri.EscapeDataString(st)}&service={Uri.EscapeDataString(service)}";
            using var response = await client.GetAsync(new Uri(baseUri, path), cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AuthenticationException("认证校验失败，请稍后重试。");
            return ParseIdentity(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException) { throw new AuthenticationException("无法连接认证服务器，请检查网络。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AuthenticationException("认证请求超时，请重试。"); }
        finally
        {
            // Do not retain a reusable campus login ticket after extracting identity.
            if (tgt is not null)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var cleanup = await client.DeleteAsync(new Uri(baseUri, $"restlet/tickets/{Uri.EscapeDataString(tgt)}"), cleanupTimeout.Token);
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<string> PostTicket(string path, HttpContent body, string prefix, CancellationToken ct)
    {
        using var response = await client.PostAsync(new Uri(baseUri, path), body, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AuthenticationException("认证未通过，请检查账号密码或联系管理员。");
        if (!response.IsSuccessStatusCode) throw new AuthenticationException("认证服务暂时不可用。");
        var ticket = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!ticket.StartsWith(prefix, StringComparison.Ordinal) || ticket.Length > 4096 || ticket.Any(char.IsWhiteSpace))
            throw new AuthenticationException("认证服务返回了不支持的票据格式。");
        return ticket;
    }

    public static Identity ParseIdentity(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024
            });
            var document = XDocument.Load(reader);
            // Prefixes (cas/sso) are aliases. Match the CAS namespace, not their spelling.
            XNamespace cas = "http://www.yale.edu/tp/cas";
            if (document.Root?.Name != cas + "serviceResponse") throw InvalidResponse();
            if (document.Root.Elements(cas + "authenticationFailure").Any()) throw InvalidResponse();
            var successes = document.Root.Elements(cas + "authenticationSuccess").ToArray();
            if (successes.Length != 1) throw InvalidResponse();
            var users = successes[0].Elements(cas + "user").ToArray();
            var names = successes[0].Descendants(cas + "USER_NAME").ToArray();
            if (users.Length != 1 || names.Length != 1) throw InvalidResponse();
            var id = users[0].Value.Trim();
            var name = names[0].Value.Trim();
            if (id.Length is 0 or > 64 || name.Length is 0 or > 128 || id.Any(char.IsControl) || name.Any(char.IsControl))
                throw InvalidResponse();
            return new Identity(id, name);
        }
        catch (XmlException) { throw InvalidResponse(); }
    }

    private static AuthenticationException InvalidResponse() => new("未取得有效的姓名和学号，请联系管理员核对认证响应。");
}
