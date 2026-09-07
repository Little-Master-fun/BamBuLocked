using System.Net;
using PrintGate.Core;
using Xunit;

namespace PrintGate.Tests;

public sealed class AuthenticationTests
{
    internal const string Success = """
        <s:serviceResponse xmlns:s="http://www.yale.edu/tp/cas">
          <s:authenticationSuccess><s:user>00123456</s:user>
            <s:attributes><s:USER_NAME>测试 &amp; 用户</s:USER_NAME></s:attributes>
          </s:authenticationSuccess>
        </s:serviceResponse>
        """;
    [Fact] public void ParsesVerifiedIdentityRegardlessOfPrefix()
        => Assert.Equal(new Identity("00123456", "测试 & 用户"), CasAuthenticator.ParseIdentity(Success));

    [Theory]
    [InlineData("<serviceResponse><user>1</user><USER_NAME>张三</USER_NAME></serviceResponse>")]
    [InlineData("<s:serviceResponse xmlns:s='http://www.yale.edu/tp/cas'><s:authenticationFailure/><s:authenticationSuccess><s:user>1</s:user><s:USER_NAME>A</s:USER_NAME></s:authenticationSuccess></s:serviceResponse>")]
    [InlineData("<s:serviceResponse xmlns:s='http://www.yale.edu/tp/cas'><s:authenticationSuccess><s:user>1</s:user></s:authenticationSuccess></s:serviceResponse>")]
    [InlineData("<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><x>&secret;</x>")]
    [InlineData("<html>login</html>")]
    public void RejectsUnverifiedOrMalformedResponse(string xml)
        => Assert.Throws<AuthenticationException>(() => CasAuthenticator.ParseIdentity(xml));

    [Fact] public void RejectsDuplicateIdentities()
        => Assert.Throws<AuthenticationException>(() => CasAuthenticator.ParseIdentity(Success.Replace("</s:user>", "</s:user><s:user>999</s:user>")));

    [Fact] public async Task EncodesCredentialsAndDeletesTicket()
    {
        var requests = new List<(string Method, string Path, string Body)>();
        using var client = new HttpClient(new FakeHandler(async request =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
            var body = requests.Count switch { 1 => "TGT-test", 2 => "ST-test", 3 => Success, _ => "" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var auth = new CasAuthenticator(client, new Uri("https://example.invalid/cas/"), "https://example.invalid/service?m=up");
        var identity = await auth.AuthenticateAsync("00123456", "a&b+c=秘密", CancellationToken.None);
        Assert.Equal("00123456", identity.StudentId);
        Assert.Equal("username=00123456&password=a%26b%2Bc%3D%E7%A7%98%E5%AF%86", requests[0].Body);
        Assert.Equal("DELETE", requests[3].Method);
        Assert.Equal("/cas/restlet/tickets/TGT-test", requests[3].Path);
        Assert.Contains("service=https%3A%2F%2Fexample.invalid", requests[2].Path);
    }

    [Fact] public async Task CleansUpTicketEvenWhenValidationFails()
    {
        var methods = new List<HttpMethod>();
        using var client = new HttpClient(new FakeHandler(request =>
        {
            methods.Add(request.Method);
            var body = methods.Count switch { 1 => "TGT-test", 2 => "ST-test", _ => "<invalid/>" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        var auth = new CasAuthenticator(client, new Uri("https://example.invalid/cas/"), "https://example.invalid/service");
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.AuthenticateAsync("1", "secret", CancellationToken.None));
        Assert.Equal(HttpMethod.Delete, methods.Last());
    }

    [Fact] public async Task RejectsNonHttpsBeforeSendingCredentials()
    {
        using var client = new HttpClient(new FakeHandler(_ => throw new Exception("must not send")));
        var auth = new CasAuthenticator(client, new Uri("http://example.invalid/cas/"), "https://example.invalid/service");
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.AuthenticateAsync("1", "secret", CancellationToken.None));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
