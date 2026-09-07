namespace PrintGate.Core;

public sealed class SessionCoordinator(AuditStore audit, string computerId)
{
    public string? SessionId { get; private set; }
    public string? RetainedOwner { get; private set; }
    public bool IsAuthorized => SessionId is not null;

    public bool CanEnter(Identity identity) => RetainedOwner is null || RetainedOwner == identity.StudentId;

    public void Enter(Identity identity, string organization)
    {
        if (IsAuthorized) throw new InvalidOperationException("已有使用会话。");
        if (!CanEnter(identity)) throw new InvalidOperationException("上次工程尚未关闭，请原操作人认证后关闭 Studio，或联系管理员。");
        SessionId = audit.Begin(identity, computerId, organization); // Audit failure must prevent access.
        RetainedOwner = identity.StudentId;
    }

    public void Lock(string reason, bool retainStudio)
    {
        var id = SessionId;
        SessionId = null; // Revoke first, even if writing the log fails.
        if (!retainStudio) RetainedOwner = null;
        if (id is not null) audit.End(id, reason);
    }
}
