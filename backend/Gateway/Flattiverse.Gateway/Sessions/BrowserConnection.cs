using System.Collections.Concurrent;
using System.Threading.Channels;
using Flattiverse.Gateway.Protocol;
using Flattiverse.Gateway.Protocol.ClientMessages;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Sessions;

public sealed class BrowserConnection : IDisposable
{
    private readonly string _connectionId;
    private readonly ILogger _logger;
    private readonly Channel<ServerMessage> _outbound;
    private readonly ConcurrentDictionary<string, PlayerSession> _attachedSessions = new();
    private string? _selectedSessionId;

    public string ConnectionId => _connectionId;
    public string? SelectedSessionId => _selectedSessionId;

    public BrowserConnection(ILogger logger)
    {
        _connectionId = Guid.NewGuid().ToString("N");
        _logger = logger;
        _outbound = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public ChannelReader<ServerMessage> OutboundReader => _outbound.Reader;

    public void EnqueueMessage(ServerMessage message)
    {
        _outbound.Writer.TryWrite(message);
    }

    public SessionReadyMessage BuildSessionReady()
    {
        var sessions = new List<PlayerSessionSummaryDto>();
        foreach (var kvp in _attachedSessions)
        {
            sessions.Add(new PlayerSessionSummaryDto
            {
                PlayerSessionId = kvp.Key,
                DisplayName = kvp.Value.DisplayName,
                Connected = kvp.Value.Connected,
                Selected = kvp.Key == _selectedSessionId,
                TeamName = kvp.Value.TeamName
            });
        }

        return new SessionReadyMessage
        {
            ConnectionId = _connectionId,
            PlayerSessions = sessions
        };
    }

    public async Task HandleMessageAsync(object? raw, PlayerSessionPool sessionPool)
    {
        var msg = ClientMessage.Parse(raw);
        if (msg is null)
        {
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "error",
                Code = "invalid_message",
                Message = "Could not parse message.",
                Recoverable = true
            });
            return;
        }

        switch (msg.Type)
        {
            case "pong":
                break;

            case "connection.attach":
                await HandleAttach(msg, sessionPool);
                break;

            case "connection.detach":
                HandleDetach(msg);
                break;

            case "player.select":
                HandleSelect(msg);
                break;

            default:
                if (msg.Type.StartsWith("command."))
                    await HandleCommand(msg);
                else
                    EnqueueMessage(new ServerStatusMessage
                    {
                        Kind = "error",
                        Code = "unknown_message_type",
                        Message = $"Unknown message type: {msg.Type}",
                        Recoverable = true
                    });
                break;
        }
    }

    private async Task HandleAttach(ClientMessage msg, PlayerSessionPool sessionPool)
    {
        var apiKey = msg.Payload?.GetProperty("apiKey").GetString();
        var teamName = msg.Payload?.TryGetProperty("teamName", out var tn) == true ? tn.GetString() : null;

        if (string.IsNullOrEmpty(apiKey) || apiKey.Length != 64)
        {
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "error",
                Code = "invalid_api_key",
                Message = "API key must be a 64-character hex string.",
                Recoverable = true
            });
            return;
        }

        try
        {
            var session = await sessionPool.GetOrCreateAsync(apiKey, teamName);
            session.AttachConnection(this);
            _attachedSessions[session.Id] = session;

            if (_selectedSessionId is null || !_attachedSessions.ContainsKey(_selectedSessionId))
                _selectedSessionId = session.Id;

            // Send session.ready
            EnqueueMessage(BuildSessionReady());

            // Send snapshot.full
            EnqueueMessage(new SnapshotFullMessage { Snapshot = session.BuildSnapshot() });

            // Send owner.delta with overlay.snapshot
            var overlayEvents = session.BuildOverlaySnapshot();
            if (overlayEvents.Count > 0)
            {
                EnqueueMessage(new OwnerDeltaMessage
                {
                    PlayerSessionId = session.Id,
                    Events = overlayEvents
                });
            }

            // Send status
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "info",
                Code = "player_session_attached",
                Message = $"Attached to session {session.DisplayName}",
                Recoverable = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach player session");
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "error",
                Code = "player_session_unavailable",
                Message = ex.Message,
                Recoverable = true
            });
        }
    }

    private void HandleDetach(ClientMessage msg)
    {
        var sessionId = msg.PlayerSessionId;
        if (sessionId is null || !_attachedSessions.TryRemove(sessionId, out var session))
        {
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "error",
                Code = "invalid_session",
                Message = "Session not found.",
                Recoverable = true
            });
            return;
        }

        session.DetachConnection(this);

        if (_selectedSessionId == sessionId)
        {
            _selectedSessionId = _attachedSessions.Keys.FirstOrDefault();
        }

        EnqueueMessage(BuildSessionReady());
        EnqueueMessage(new ServerStatusMessage
        {
            Kind = "info",
            Code = "player_session_detached",
            Message = $"Detached from session {session.DisplayName}",
            Recoverable = true
        });

        if (_selectedSessionId is not null && _attachedSessions.TryGetValue(_selectedSessionId, out var newSelected))
        {
            EnqueueMessage(new SnapshotFullMessage { Snapshot = newSelected.BuildSnapshot() });
            var overlayEvents = newSelected.BuildOverlaySnapshot();
            if (overlayEvents.Count > 0)
            {
                EnqueueMessage(new OwnerDeltaMessage
                {
                    PlayerSessionId = newSelected.Id,
                    Events = overlayEvents
                });
            }
        }
    }

    private void HandleSelect(ClientMessage msg)
    {
        var sessionId = msg.PlayerSessionId;
        if (sessionId is null || !_attachedSessions.TryGetValue(sessionId, out var session))
        {
            EnqueueMessage(new ServerStatusMessage
            {
                Kind = "error",
                Code = "invalid_session",
                Message = "Session not found or not attached.",
                Recoverable = true
            });
            return;
        }

        _selectedSessionId = sessionId;

        EnqueueMessage(BuildSessionReady());
        EnqueueMessage(new SnapshotFullMessage { Snapshot = session.BuildSnapshot() });

        var overlayEvents = session.BuildOverlaySnapshot();
        if (overlayEvents.Count > 0)
        {
            EnqueueMessage(new OwnerDeltaMessage
            {
                PlayerSessionId = session.Id,
                Events = overlayEvents
            });
        }

        EnqueueMessage(new ServerStatusMessage
        {
            Kind = "info",
            Code = "player_session_selected",
            Message = $"Selected session {session.DisplayName}",
            Recoverable = true
        });
    }

    private async Task HandleCommand(ClientMessage msg)
    {
        if (_selectedSessionId is null || !_attachedSessions.TryGetValue(_selectedSessionId, out var session))
        {
            EnqueueMessage(new Protocol.ServerMessages.CommandReplyMessage
            {
                CommandId = msg.CommandId ?? "",
                Status = "rejected",
                Error = new ErrorInfoDto { Code = "no_session", Message = "No player session selected.", Recoverable = true }
            });
            return;
        }

        var reply = await session.HandleCommandAsync(msg.Type, msg.CommandId ?? "", msg.Payload);
        EnqueueMessage(reply);
    }

    public void Dispose()
    {
        _outbound.Writer.TryComplete();

        foreach (var kvp in _attachedSessions)
            kvp.Value.DetachConnection(this);

        _attachedSessions.Clear();
    }
}
