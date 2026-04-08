namespace Flattiverse.Gateway.Domain.Sessions;

public sealed class BrowserSession
{
    private readonly Dictionary<string, AttachedPlayerSession> attachedPlayerSessions =
        new(StringComparer.Ordinal);

    public BrowserSession(string connectionId, DateTimeOffset createdAtUtc)
    {
        ConnectionId = connectionId;
        CreatedAtUtc = createdAtUtc;
        LastSeenAtUtc = createdAtUtc;
    }

    public string ConnectionId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public string? SelectedPlayerSessionId { get; private set; }

    public bool ObserverOnly => attachedPlayerSessions.Count == 0;

    public IReadOnlyCollection<AttachedPlayerSession> AttachedPlayerSessions => attachedPlayerSessions.Values.ToArray();

    public void Touch(DateTimeOffset now) => LastSeenAtUtc = now;

    public void AttachPlayerSession(AttachedPlayerSession playerSession, bool selectPlayerSession)
    {
        attachedPlayerSessions[playerSession.PlayerSessionId] = playerSession;

        if (selectPlayerSession || SelectedPlayerSessionId is null)
        {
            SelectedPlayerSessionId = playerSession.PlayerSessionId;
        }
    }

    public bool SelectPlayerSession(string playerSessionId)
    {
        if (!attachedPlayerSessions.ContainsKey(playerSessionId))
        {
            return false;
        }

        SelectedPlayerSessionId = playerSessionId;
        return true;
    }

    public bool DetachPlayerSession(string playerSessionId)
    {
        var removed = attachedPlayerSessions.Remove(playerSessionId);

        if (!removed)
        {
            return false;
        }

        if (SelectedPlayerSessionId == playerSessionId)
        {
            SelectedPlayerSessionId = attachedPlayerSessions.Keys.OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault();
        }

        return true;
    }

    public bool HasPlayerSession(string playerSessionId) => attachedPlayerSessions.ContainsKey(playerSessionId);
}