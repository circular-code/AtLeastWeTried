using System.Text.Json.Nodes;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Application.Services;
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Domain.Sessions;
using Flattiverse.Gateway.Infrastructure.Sessions;

namespace Flattiverse.Gateway.Tests.Unit;

public sealed class GatewaySessionOrchestratorTests
{
    [Fact]
    public async Task OpenConnectionAsync_EmitsSessionReadyAndAttachRequired()
    {
        var harness = CreateHarness();
        var orchestrator = harness.Orchestrator;

        var opened = await orchestrator.OpenConnectionAsync(CancellationToken.None);

        Assert.Collection(
            opened.Messages,
            message => Assert.IsType<SessionReadyMessage>(message),
            message =>
            {
                var status = Assert.IsType<StatusMessage>(message);
                Assert.Equal("attach_required", status.Code);
            });
    }

    [Fact]
    public async Task AttachAsync_EmitsBootstrapSequence()
    {
        var harness = CreateHarness();
        var orchestrator = harness.Orchestrator;
        var opened = await orchestrator.OpenConnectionAsync(CancellationToken.None);

        var messages = await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new ConnectionAttachMessage(new AttachPayload(new string('a', 64), "Blue")),
            CancellationToken.None);

        Assert.Collection(
            messages,
            message => Assert.IsType<SessionReadyMessage>(message),
            message => Assert.IsType<SnapshotFullMessage>(message),
            message => Assert.IsType<OwnerDeltaMessage>(message),
            message =>
            {
                var status = Assert.IsType<StatusMessage>(message);
                Assert.Equal("player_session_created", status.Code);
            });

        var snapshot = Assert.IsType<SnapshotFullMessage>(messages[1]);
        Assert.NotEmpty(snapshot.Snapshot.Clusters);
        Assert.NotEmpty(snapshot.Snapshot.Units);
        Assert.NotEmpty(snapshot.Snapshot.Controllables);
    }

    [Fact]
    public async Task CommandCreateShip_ReturnsRuntimeRefreshAndCompletedReply()
    {
        var harness = CreateHarness();
        var orchestrator = harness.Orchestrator;
        var opened = await orchestrator.OpenConnectionAsync(CancellationToken.None);

        await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new ConnectionAttachMessage(new AttachPayload(new string('a', 64), "Blue")),
            CancellationToken.None);

        var messages = await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new CommandMessage(
                "command.create_ship",
                "create-001",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = "Aurora Wing",
                    ["shipClass"] = "modern"
                }),
            CancellationToken.None);

        Assert.Collection(
            messages,
            message => Assert.IsType<SnapshotFullMessage>(message),
            message => Assert.IsType<OwnerDeltaMessage>(message),
            message =>
            {
                var reply = Assert.IsType<CommandReplyMessage>(message);
                Assert.Equal("completed", reply.Status);
                Assert.Null(reply.Error);
            });
    }

    [Fact]
    public async Task DetachAsync_ReleasesPlayerSessionLease()
    {
        var harness = CreateHarness();
        var orchestrator = harness.Orchestrator;
        var opened = await orchestrator.OpenConnectionAsync(CancellationToken.None);

        var attachMessages = await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new ConnectionAttachMessage(new AttachPayload(new string('a', 64), "Blue")),
            CancellationToken.None);

        var sessionReady = Assert.IsType<SessionReadyMessage>(attachMessages[0]);
        var playerSessionId = Assert.Single(sessionReady.PlayerSessions).PlayerSessionId;

        await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new ConnectionDetachMessage(playerSessionId),
            CancellationToken.None);

        Assert.Equal(0, harness.PlayerSessionPool.GetHolderCount(playerSessionId));
    }

    [Fact]
    public async Task DisconnectAsync_ReleasesAttachedPlayerSessions()
    {
        var harness = CreateHarness();
        var orchestrator = harness.Orchestrator;
        var opened = await orchestrator.OpenConnectionAsync(CancellationToken.None);

        var attachMessages = await orchestrator.HandleClientMessageAsync(
            opened.ConnectionId,
            new ConnectionAttachMessage(new AttachPayload(new string('a', 64), "Blue")),
            CancellationToken.None);

        var sessionReady = Assert.IsType<SessionReadyMessage>(attachMessages[0]);
        var playerSessionId = Assert.Single(sessionReady.PlayerSessions).PlayerSessionId;

        await orchestrator.DisconnectAsync(opened.ConnectionId, CancellationToken.None);

        Assert.Equal(0, harness.PlayerSessionPool.GetHolderCount(playerSessionId));
    }

    private static TestHarness CreateHarness()
    {
        var runtime = new FakeGatewayRuntime();
        var playerSessionPool = new FakePlayerSessionPool();

        return new TestHarness(
            new GatewaySessionOrchestrator(
                new InMemoryBrowserSessionStore(),
                runtime,
                playerSessionPool,
                TimeProvider.System),
            playerSessionPool,
            runtime);
    }

    private sealed record TestHarness(
        GatewaySessionOrchestrator Orchestrator,
        FakePlayerSessionPool PlayerSessionPool,
        FakeGatewayRuntime Runtime);

    private sealed class FakePlayerSessionPool : IPlayerSessionPool
    {
        private readonly Dictionary<string, AttachedPlayerSession> sessionsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> holdersByPlayerSessionId = new(StringComparer.Ordinal);

        public ValueTask<AttachedPlayerSession> AttachAsync(
            string connectionId,
            string apiKey,
            string? teamName,
            CancellationToken cancellationToken)
        {
            var key = $"{apiKey}:{teamName}";
            if (!sessionsByKey.TryGetValue(key, out var session))
            {
                session = new AttachedPlayerSession($"player-{sessionsByKey.Count + 1:D4}", $"Session-{apiKey[..6]}", teamName, true);
                sessionsByKey[key] = session;
                holdersByPlayerSessionId[session.PlayerSessionId] = new HashSet<string>(StringComparer.Ordinal);
            }

            holdersByPlayerSessionId[session.PlayerSessionId].Add(connectionId);
            return ValueTask.FromResult(session);
        }

        public ValueTask ReleaseAsync(string connectionId, string playerSessionId, CancellationToken cancellationToken)
        {
            if (holdersByPlayerSessionId.TryGetValue(playerSessionId, out var holders))
            {
                holders.Remove(connectionId);
            }

            return ValueTask.CompletedTask;
        }

        public int GetHolderCount(string playerSessionId)
        {
            return holdersByPlayerSessionId.TryGetValue(playerSessionId, out var holders) ? holders.Count : 0;
        }
    }

    private sealed class FakeGatewayRuntime : IGatewayRuntime
    {
        public ValueTask EnsurePlayerSessionAsync(AttachedPlayerSession playerSession, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorldSnapshot> GetSnapshotAsync(string playerSessionId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new WorldSnapshot(
                "Test Galaxy",
                "Gateway unit test snapshot.",
                "mission",
                [new TeamSnapshot(1, "Blue", 0, "#3366FF")],
                [new ClusterSnapshot(1, "Alpha", true, true)],
                [new UnitSnapshot("unit-001", 1, "sun", 0, 0, 0, 50, null, 800, 200, 100, 12, 5)],
                [new PublicControllableSnapshot("player-0001/c/001", "Aurora Wing", "Blue", true, 100)]));
        }

        public ValueTask<OwnerDeltaMessage> GetOwnerOverlayAsync(string playerSessionId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new OwnerDeltaMessage(
                playerSessionId,
                [
                    new OverlayEvent("overlay.snapshot", null, null),
                    new OverlayEvent(
                        "overlay.updated",
                        "player-0001/c/001",
                        new JsonObject
                        {
                            ["displayName"] = "Aurora Wing",
                            ["alive"] = true
                        })
                ]));
        }

        public ValueTask<IReadOnlyList<GatewayMessage>> ExecuteCommandAsync(
            string playerSessionId,
            CommandMessage command,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<GatewayMessage> messages =
            [
                new SnapshotFullMessage(GetSnapshotAsync(playerSessionId, cancellationToken).Result),
                GetOwnerOverlayAsync(playerSessionId, cancellationToken).Result,
                new CommandReplyMessage(
                    command.CommandId ?? "generated-command",
                    "completed",
                    new JsonObject
                    {
                        ["action"] = "created",
                        ["controllableId"] = "player-0001/c/001"
                    },
                    null)
            ];

            return ValueTask.FromResult(messages);
        }

        public ValueTask TickAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}