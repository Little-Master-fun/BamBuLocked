using PrintGate.Core;
using Xunit;

public sealed class LocalAdministratorTests
{
    private static readonly LocalAdministratorCredential Credential = LocalAdministratorCredential.Create("MyAdmin", "test-only-password-123!");
    private sealed class Campus : IAuthenticator
    {
        public int Calls;
        public Task<Identity> AuthenticateAsync(string account, string password, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new Identity("001", "Campus user")); }
    }
    [Fact]
    public void HashIsSaltedAndDoesNotStorePassword()
    {
        var other = LocalAdministratorCredential.Create("MyAdmin", "test-only-password-123!");
        Assert.NotEqual(Credential.Salt,other.Salt); Assert.NotEqual(Credential.PasswordHash,other.PasswordHash);
        Assert.True(Credential.Verify("test-only-password-123!")); Assert.False(Credential.Verify("wrong-password"));
        Assert.DoesNotContain("test-only-password",System.Text.Json.JsonSerializer.Serialize(Credential));
    }
    [Fact]
    public async Task LocalSuccessAndFailureNeverCallCampus()
    {
        var campus = new Campus(); var login = new LoginAuthenticator(campus,Credential);
        Assert.True((await login.AuthenticateAsync("myadmin","test-only-password-123!",true,default)).IsLocalAdministrator);
        await Assert.ThrowsAsync<AuthenticationException>(()=>login.AuthenticateAsync("MyAdmin","wrong-password",false,default));
        await Assert.ThrowsAsync<AuthenticationException>(()=>login.AuthenticateAsync("typo","wrong-password",true,default));
        Assert.Equal(0,campus.Calls);
    }
    [Fact]
    public async Task CampusUsersKeepExistingAuthentication()
    {
        var campus = new Campus(); var result = await new LoginAuthenticator(campus,Credential).AuthenticateAsync("001","campus-password",false,default);
        Assert.False(result.IsLocalAdministrator); Assert.Equal(1,campus.Calls);
    }
    [Fact]
    public async Task UnconfiguredAdministratorIsDeniedWithoutNetwork()
    {
        var campus = new Campus();
        await Assert.ThrowsAsync<AuthenticationException>(()=>new LoginAuthenticator(campus,null).AuthenticateAsync("admin","password",true,default));
        Assert.Equal(0,campus.Calls);
    }
    [Fact]
    public async Task FiveFailuresBlockEvenCorrectPasswordUntilCooldown()
    {
        var time = DateTimeOffset.UtcNow; var login = new LoginAuthenticator(new Campus(),Credential,()=>time);
        for (var i=0;i<5;i++) await Assert.ThrowsAsync<AuthenticationException>(()=>login.AuthenticateAsync("wrong-name","wrong",true,default));
        await Assert.ThrowsAsync<AuthenticationException>(()=>login.AuthenticateAsync("MyAdmin","test-only-password-123!",true,default));
        time=time.AddSeconds(31);
        Assert.True((await login.AuthenticateAsync("MyAdmin","test-only-password-123!",true,default)).IsLocalAdministrator);
    }
    [Fact]
    public void InvalidConfigurationAndWeakInputAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>LocalAdministratorCredential.Create("x","long-password-123"));
        Assert.Throws<ArgumentException>(()=>LocalAdministratorCredential.Create("admin","short"));
        Assert.Throws<InvalidDataException>(()=>(Credential with { Iterations=1 }).Validate());
        Assert.Throws<InvalidDataException>(()=>(Credential with { PasswordHash="invalid" }).Validate());
        Assert.Throws<InvalidDataException>(()=>(Credential with { Version=2 }).Validate());
    }
}
