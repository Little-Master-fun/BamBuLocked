using System.Security.Cryptography;

namespace PrintGate.Core;

public sealed record LocalAdministratorCredential(int Version, string Username, string Salt, string PasswordHash, int Iterations)
{
    public const int WorkFactor = 600_000;
    public static LocalAdministratorCredential Create(string username, string password)
    {
        username = username.Trim();
        if (username.Length is < 3 or > 64 || username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("用户名需为 3–64 个字符，不能包含空格或控制字符。");
        if (password.Length is < 10 or > 256 || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("密码需为 10–256 个字符，不能全部为空白。");
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, WorkFactor, HashAlgorithmName.SHA256, 32);
        return new(1, username, Convert.ToBase64String(salt), Convert.ToBase64String(hash), WorkFactor);
    }
    public bool MatchesUsername(string username) => string.Equals(Username, username.Trim(), StringComparison.OrdinalIgnoreCase);
    public void Validate()
    {
        if (Version != 1 || Iterations != WorkFactor || string.IsNullOrWhiteSpace(Username) || Username.Length is < 3 or > 64
            || Username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || Salt is null || PasswordHash is null)
            throw new InvalidDataException("本地管理员配置格式无效，请使用设置工具重新保存。");
        try
        {
            if (Convert.FromBase64String(Salt).Length != 32 || Convert.FromBase64String(PasswordHash).Length != 32)
                throw new InvalidDataException("本地管理员配置格式无效。");
        }
        catch (FormatException) { throw new InvalidDataException("本地管理员配置格式无效。"); }
    }
    public bool Verify(string password)
    {
        Validate();
        if (password.Length > 256) return false;
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(Salt), Iterations, HashAlgorithmName.SHA256, 32);
        try { return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(PasswordHash)); }
        finally { CryptographicOperations.ZeroMemory(computed); }
    }
}

public sealed record LoginResult(Identity Identity, bool IsLocalAdministrator);

public sealed record CachedCampusIdentity(int Version, string Account, Identity Identity)
{
    public static CachedCampusIdentity Create(string account, Identity identity)
    {
        account = account.Trim();
        if (string.IsNullOrWhiteSpace(account) || account.Length > 64 || account.Any(char.IsControl))
            throw new ArgumentException("账号格式无效。");
        return new(1, account, identity);
    }
    public bool MatchesAccount(string account) => string.Equals(Account, account.Trim(), StringComparison.OrdinalIgnoreCase);
    public void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Account) || Account.Length > 64 || Account.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(Identity.StudentId) || Identity.StudentId.Length > 64 || Identity.StudentId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(Identity.Name) || Identity.Name.Length > 128 || Identity.Name.Any(char.IsControl))
            throw new InvalidDataException("本地认证缓存格式无效。");
    }
}

public interface ICampusCredentialCache
{
    CachedCampusIdentity? Find(string account);
    void Save(CachedCampusIdentity identity);
}

// Local credentials are never sent to the campus authentication service, even on failure.
public sealed class LoginAuthenticator(IAuthenticator campus, LocalAdministratorCredential? local, ICampusCredentialCache? campusCache = null, Func<DateTimeOffset>? now = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int failures;
    private DateTimeOffset blockedUntil;
    public async Task<LoginResult> AuthenticateAsync(string account, string password, bool localMode, CancellationToken cancellationToken)
    {
        if (!localMode && local?.MatchesUsername(account) != true)
        {
            try
            {
                var identity = await campus.AuthenticateAsync(account, password, cancellationToken);
                campusCache?.Save(CachedCampusIdentity.Create(account, identity));
                return new(identity, false);
            }
            catch (CampusServiceUnavailableException)
            {
                var cached = campusCache?.Find(account);
                if (cached is not null) return new(cached.Identity, false);
                throw;
            }
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var clock = now?.Invoke() ?? DateTimeOffset.UtcNow;
            if (clock < blockedUntil) throw new AuthenticationException("本地管理员登录失败次数过多，请 30 秒后重试。");
            var verified = local is not null && local.MatchesUsername(account) && await Task.Run(() => local.Verify(password), cancellationToken);
            if (!verified)
            {
                if (++failures >= 5) { failures = 0; blockedUntil = clock.AddSeconds(30); }
                throw new AuthenticationException("本地管理员用户名或密码错误，或尚未配置管理员。");
            }
            failures = 0;
            return new(new(local!.Username, local.Username), true);
        }
        finally { gate.Release(); }
    }
}
