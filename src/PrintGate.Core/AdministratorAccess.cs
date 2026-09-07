namespace PrintGate.Core;

public static class AdministratorAccess
{
    // Only call with a successfully verified server identity, never the login form account.
    public static bool IsAllowed(Identity verifiedIdentity, IEnumerable<string>? studentIds) =>
        !string.IsNullOrWhiteSpace(verifiedIdentity.StudentId) && studentIds is not null &&
        studentIds.Any(id => !string.IsNullOrWhiteSpace(id) && string.Equals(id.Trim(), verifiedIdentity.StudentId, StringComparison.Ordinal));
}
