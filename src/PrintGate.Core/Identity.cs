namespace PrintGate.Core;

public sealed record Identity(string StudentId, string Name);
public sealed class AuthenticationException(string message) : Exception(message);
public interface IAuthenticator
{
    Task<Identity> AuthenticateAsync(string account, string password, CancellationToken cancellationToken);
}
