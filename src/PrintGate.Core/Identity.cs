namespace PrintGate.Core;

public sealed record Identity(string StudentId, string Name);
public class AuthenticationException(string message) : Exception(message);
public sealed class CampusServiceUnavailableException(string message) : AuthenticationException(message);
public interface IAuthenticator
{
    Task<Identity> AuthenticateAsync(string account, string password, CancellationToken cancellationToken);
}
