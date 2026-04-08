using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Contracts.Protocol;
using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Application.Services;

public sealed class GatewaySessionOrchestrator
{
    private readonly IBrowserSessionStore browserSessionStore;
    private readonly IGatewayRuntime gatewayRuntime;
    private readonly IPlayerSessionPool playerSessionPool;
    private readonly TimeProvider timeProvider;

    public GatewaySessionOrchestrator(
        IBrowserSessionStore browserSessionStore,
        IGatewayRuntime gatewayRuntime,
        IPlayerSessionPool playerSessionPool,
        TimeProvider timeProvider)
    {
        this.browserSessionStore = browserSessionStore;
        this.gatewayRuntime = gatewayRuntime;
        this.playerSessionPool = playerSessionPool;
        this.timeProvider = timeProvider;
    }

    public async ValueTask<ConnectionOpened> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var session = new BrowserSession(connectionId, timeProvider.GetUtcNow());

        await browserSessionStore.UpsertAsync(session, cancellationToken);

        var messages = new GatewayMessage[]
        {
            BuildSessionReady(session),
            BuildStatus(GatewayStatusKind.Warning, "attach_required", "Attach a Flattiverse API key to open or reuse a pooled player session.", true)
        };

        return new ConnectionOpened(connectionId, messages);
    }

    public async ValueTask<IReadOnlyList<GatewayMessage>> HandleClientMessageAsync(
        string connectionId,
        ClientMessage message,
        CancellationToken cancellationToken)
    {
        var session = await browserSessionStore.GetAsync(connectionId, cancellationToken);

        if (session is null)
        {
            return new GatewayMessage[]
            {
                BuildStatus(GatewayStatusKind.Error, "session_not_found", "The browser session is no longer available.", false)
            };
        }

        session.Touch(timeProvider.GetUtcNow());

        IReadOnlyList<GatewayMessage> response;

        switch (message)
        {
            case ConnectionAttachMessage attach:
                response = await HandleAttachAsync(session, attach, cancellationToken);
                break;
            case ConnectionDetachMessage detach:
                response = await HandleDetachAsync(session, detach, cancellationToken);
                break;
            case PlayerSelectMessage select:
                response = await HandleSelectAsync(session, select, cancellationToken);
                break;
            case CommandMessage command:
                response = await HandleCommand(session, command, cancellationToken);
                break;
            case PongMessage:
                response = Array.Empty<GatewayMessage>();
                break;
            default:
                response =
                [
                    BuildStatus(GatewayStatusKind.Warning, "unknown_message_type", $"Unsupported message type '{message.Type}'.", true)
                ];
                break;
        }

        await browserSessionStore.UpsertAsync(session, cancellationToken);
        return response;
    }

    public ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken)
    {
        return DisconnectCoreAsync(connectionId, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<GatewayMessage>> HandleAttachAsync(
        BrowserSession session,
        ConnectionAttachMessage message,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeApiKey(message.Payload?.ApiKey))
        {
            return new GatewayMessage[]
            {
                BuildStatus(GatewayStatusKind.Warning, "invalid_api_key", "API keys must be exactly 64 hexadecimal characters.", true)
            };
        }

        AttachedPlayerSession? attachedPlayerSession = null;

        try
        {
            attachedPlayerSession = await playerSessionPool.AttachAsync(
                session.ConnectionId,
                message.Payload!.ApiKey!,
                message.Payload.TeamName,
                cancellationToken);

            await gatewayRuntime.EnsurePlayerSessionAsync(attachedPlayerSession, cancellationToken);
            session.AttachPlayerSession(attachedPlayerSession, selectPlayerSession: true);

            return await BuildAttachOrSelectSequenceAsync(
                session,
                attachedPlayerSession.PlayerSessionId,
                "player_session_created",
                "Player session attached to the browser connection.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            if (attachedPlayerSession is not null)
            {
                await playerSessionPool.ReleaseAsync(session.ConnectionId, attachedPlayerSession.PlayerSessionId, cancellationToken);
            }

            return
            [
                BuildStatus(GatewayStatusKind.Error, "player_session_attach_failed", exception.Message, true)
            ];
        }
    }

    private async ValueTask<IReadOnlyList<GatewayMessage>> HandleSelectAsync(
        BrowserSession session,
        PlayerSelectMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.PlayerSessionId) || !session.SelectPlayerSession(message.PlayerSessionId))
        {
            return
                new GatewayMessage[]
                {
                    BuildStatus(GatewayStatusKind.Warning, "player_session_unavailable", "The requested player session is not attached to this browser connection.", true)
                };
        }

        try
        {
            return await BuildAttachOrSelectSequenceAsync(
                session,
                message.PlayerSessionId,
                "player_session_selected",
                "Player session selected for command routing.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            return
            [
                BuildSessionReady(session),
                BuildStatus(GatewayStatusKind.Error, "player_session_select_failed", exception.Message, true)
            ];
        }
    }

    private async ValueTask<IReadOnlyList<GatewayMessage>> HandleDetachAsync(
        BrowserSession session,
        ConnectionDetachMessage message,
        CancellationToken cancellationToken)
    {
        var playerSessionId = string.IsNullOrWhiteSpace(message.PlayerSessionId)
            ? session.SelectedPlayerSessionId
            : message.PlayerSessionId;

        if (playerSessionId is null || !session.DetachPlayerSession(playerSessionId))
        {
            return new GatewayMessage[]
            {
                BuildStatus(GatewayStatusKind.Warning, "player_session_unavailable", "The requested player session is not attached to this browser connection.", true)
            };
        }

        await playerSessionPool.ReleaseAsync(session.ConnectionId, playerSessionId, cancellationToken);

        var messages = new List<GatewayMessage>
        {
            BuildSessionReady(session),
            BuildStatus(GatewayStatusKind.Info, "player_session_detached", "Player session detached from the browser connection.", true)
        };

        if (session.SelectedPlayerSessionId is not null)
        {
            try
            {
                messages.Add(new SnapshotFullMessage(await gatewayRuntime.GetSnapshotAsync(session.SelectedPlayerSessionId, cancellationToken)));
                messages.Add(await gatewayRuntime.GetOwnerOverlayAsync(session.SelectedPlayerSessionId, cancellationToken));
            }
            catch (Exception exception)
            {
                messages.Add(BuildStatus(GatewayStatusKind.Warning, "player_session_refresh_failed", exception.Message, true));
            }
        }

        return messages;
    }

    private ValueTask<IReadOnlyList<GatewayMessage>> HandleCommand(BrowserSession session, CommandMessage message, CancellationToken cancellationToken)
    {
        var commandId = string.IsNullOrWhiteSpace(message.CommandId)
            ? Guid.NewGuid().ToString("N")
            : message.CommandId;

        if (session.SelectedPlayerSessionId is null)
        {
            return ValueTask.FromResult<IReadOnlyList<GatewayMessage>>(
            [
                new CommandReplyMessage(
                    commandId,
                    "rejected",
                    null,
                    new CommandError(
                        "missing_player_session",
                        "Attach and select a player session before sending commands.",
                        true))
            ]);
        }

        return gatewayRuntime.ExecuteCommandAsync(session.SelectedPlayerSessionId, message with { CommandId = commandId }, cancellationToken);
    }

    private async ValueTask<GatewayMessage[]> BuildAttachOrSelectSequenceAsync(
        BrowserSession session,
        string playerSessionId,
        string statusCode,
        string statusMessage,
        CancellationToken cancellationToken)
    {
        var snapshot = await gatewayRuntime.GetSnapshotAsync(playerSessionId, cancellationToken);
        var ownerOverlay = await gatewayRuntime.GetOwnerOverlayAsync(playerSessionId, cancellationToken);

        return
        [
            BuildSessionReady(session),
            new SnapshotFullMessage(snapshot),
            ownerOverlay,
            BuildStatus(GatewayStatusKind.Info, statusCode, statusMessage, true)
        ];
    }

    private SessionReadyMessage BuildSessionReady(BrowserSession session)
    {
        var playerSessions = session.AttachedPlayerSessions
            .OrderBy(playerSession => playerSession.PlayerSessionId, StringComparer.Ordinal)
            .Select(playerSession => new PlayerSessionSummary(
                playerSession.PlayerSessionId,
                playerSession.DisplayName,
                playerSession.Connected,
                playerSession.PlayerSessionId == session.SelectedPlayerSessionId,
                playerSession.TeamName))
            .ToArray();

        return new SessionReadyMessage(
            session.ConnectionId,
            ProtocolConstants.CurrentVersion,
            session.ObserverOnly,
            playerSessions);
    }

    private static StatusMessage BuildStatus(GatewayStatusKind kind, string code, string message, bool recoverable)
    {
        return new StatusMessage(kind, code, message, recoverable);
    }

    private static bool LooksLikeApiKey(string? apiKey)
    {
        return apiKey is { Length: 64 } && apiKey.All(static character => Uri.IsHexDigit(character));
    }

    private async ValueTask DisconnectCoreAsync(string connectionId, CancellationToken cancellationToken)
    {
        var session = await browserSessionStore.GetAsync(connectionId, cancellationToken);

        if (session is not null)
        {
            foreach (var playerSession in session.AttachedPlayerSessions)
            {
                await playerSessionPool.ReleaseAsync(connectionId, playerSession.PlayerSessionId, cancellationToken);
            }
        }

        await browserSessionStore.RemoveAsync(connectionId, cancellationToken);
    }
}