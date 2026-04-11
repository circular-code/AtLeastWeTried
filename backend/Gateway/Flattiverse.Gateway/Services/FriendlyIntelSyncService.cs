using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Synchronizes MappingService intel between friendly player sessions by using
/// the game's private binary-chat transport.
/// </summary>
public sealed class FriendlyIntelSyncService
{
    private const int ProtocolVersion = 1;
    private const int MaximumSinglePayloadBytes = 1024;
    private const int MaximumBulkPayloadCount = 32;
    private const int MaximumProcessedMessagesPerScope = 4096;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FVIS");
    private static readonly object ProcessedMessageSync = new();
    private static readonly Dictionary<string, ScopeProcessedMessages> ProcessedMessagesByScope = new(StringComparer.Ordinal);
    private static readonly MessagePackSerializerOptions SyncMessagePackOptions = MessagePackSerializerOptions.Standard
        .WithResolver(ContractlessStandardResolver.Instance);

    private readonly string _sessionId;
    private readonly MappingService _mappingService;
    private readonly Func<SyncContext?> _contextResolver;
    private readonly ILogger _logger;
    private readonly object _peerStateSync = new();
    private readonly Dictionary<int, PeerState> _peerStates = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private ulong _nextMessageId;

    public FriendlyIntelSyncService(
        string sessionId,
        MappingService mappingService,
        Func<SyncContext?> contextResolver,
        ILogger logger)
    {
        _sessionId = sessionId;
        _mappingService = mappingService;
        _contextResolver = contextResolver;
        _logger = logger;
        _nextMessageId = BinaryPrimitives.ReadUInt64LittleEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|friendly-intel")).AsSpan(0, sizeof(ulong)));
    }

    public readonly record struct SyncContext(
        string TeamScopeKey,
        string GalaxyId,
        string TeamName,
        int LocalPlayerId,
        Galaxy Galaxy);

    internal enum IntelChangeKind : byte
    {
        Upsert = 1,
        Remove = 2
    }

    internal readonly record struct IntelChange(
        IntelChangeKind Kind,
        string UnitId,
        UnitSnapshotDto? Snapshot,
        string? Signature);

    private enum PacketKind : byte
    {
        Acknowledgement = 1,
        IntelDelta = 2
    }

    private readonly record struct EncodedPayload(byte[] Payload, List<IntelChange> Changes);

    private readonly record struct DecodedPacket(
        PacketKind Kind,
        ulong MessageId,
        ulong AcknowledgedMessageId,
        List<IntelChange> Changes);

    private sealed class PeerState
    {
        public bool OutboundAcknowledged { get; set; }

        public bool AwaitingAcknowledgement { get; set; }

        public bool HasSentBinaryToPeer { get; set; }

        public bool IncomingAcknowledgementInFlight { get; set; }

        public Dictionary<string, string> LastSentSignaturesByUnit { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ScopeProcessedMessages
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public Queue<string> Order { get; } = new();
    }

    public async Task FlushAsync()
    {
        if (!_flushLock.Wait(0))
            return;

        try
        {
            var context = _contextResolver();
            if (context is null || string.IsNullOrWhiteSpace(context.Value.TeamScopeKey))
                return;

            if (!LocalTeamSessionRegistry.IsLeader(context.Value.TeamScopeKey, _sessionId))
                return;

            var localUnits = BuildShareableLocalUnits(context.Value);
            var recipients = ResolveRemoteRecipients(context.Value).ToList();
            if (recipients.Count == 0)
                return;

            foreach (var recipient in recipients)
            {
                await SyncRecipientAsync(context.Value, recipient, localUnits).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public Task? HandleIncoming(PlayerBinaryChatEvent binaryEvent)
    {
        var context = _contextResolver();
        if (context is null || !IsFriendlyPeer(context.Value, binaryEvent.Player))
            return null;

        PeerState peerState;
        lock (_peerStateSync)
        {
            peerState = GetOrCreatePeerState(binaryEvent.Player.Id);
            peerState.OutboundAcknowledged = true;
            peerState.AwaitingAcknowledgement = false;
        }

        if (!TryDecodePacket(context.Value.GalaxyId, binaryEvent.Message, out var packet))
            return null;

        if (packet.Kind == PacketKind.Acknowledgement)
            return null;

        if (TryMarkMessageProcessed(context.Value.TeamScopeKey, binaryEvent.Player.Id, packet.MessageId))
        {
            _mappingService.ApplyRemoteIntel(
                binaryEvent.Player.Id.ToString(CultureInfo.InvariantCulture),
                packet.Changes);
        }

        lock (_peerStateSync)
        {
            if (peerState.HasSentBinaryToPeer || peerState.IncomingAcknowledgementInFlight)
                return null;

            peerState.IncomingAcknowledgementInFlight = true;
        }

        return SendAcknowledgementAsync(binaryEvent.Player, binaryEvent.Player.Id, context.Value.GalaxyId, packet.MessageId);
    }

    internal static bool TryDecodeForTests(string galaxyId, byte[] payload, out List<IntelChange> changes)
    {
        changes = new List<IntelChange>();
        if (!TryDecodePacket(galaxyId, payload, out var packet))
            return false;

        if (packet.Kind != PacketKind.IntelDelta)
            return false;

        changes = packet.Changes;
        return true;
    }

    internal static byte[] EncodeForTests(string galaxyId, IReadOnlyList<IntelChange> changes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(galaxyId));
        var messageId = BinaryPrimitives.ReadUInt64LittleEndian(hash.GetHashAndReset().AsSpan(0, sizeof(ulong)));

        var buffer = new ArrayBufferWriter<byte>();
        WriteHeader(buffer, PacketKind.IntelDelta, messageId, galaxyId);
        WriteUInt16(buffer, checked((ushort)changes.Count));
        for (var index = 0; index < changes.Count; index++)
            WriteChange(buffer, changes[index]);

        return buffer.WrittenSpan.ToArray();
    }

    private async Task SyncRecipientAsync(SyncContext context, Player recipient, IReadOnlyList<UnitSnapshotDto> localUnits)
    {
        List<IntelChange> pendingChanges;
        PeerState state;
        bool outboundAcknowledged;
        bool awaitingAcknowledgement;

        lock (_peerStateSync)
        {
            state = GetOrCreatePeerState(recipient.Id);
            pendingChanges = BuildPendingChanges(localUnits, state.LastSentSignaturesByUnit);
            outboundAcknowledged = state.OutboundAcknowledged;
            awaitingAcknowledgement = state.AwaitingAcknowledgement;
        }

        if (pendingChanges.Count == 0)
            return;

        if (!outboundAcknowledged && awaitingAcknowledgement)
            return;

        try
        {
            var payloads = BuildEncodedPayloads(context.GalaxyId, pendingChanges);
            if (payloads.Count == 0)
                return;

            if (!outboundAcknowledged)
            {
                var firstPayload = payloads[0];
                await recipient.Chat(firstPayload.Payload).ConfigureAwait(false);
                MarkSuccessfulSend(recipient.Id, firstPayload.Changes, outboundAcknowledged: false);
                return;
            }

            for (var index = 0; index < payloads.Count; index += MaximumBulkPayloadCount)
            {
                var batch = payloads
                    .Skip(index)
                    .Take(MaximumBulkPayloadCount)
                    .ToList();

                if (batch.Count == 1)
                {
                    await recipient.Chat(batch[0].Payload).ConfigureAwait(false);
                }
                else
                {
                    await recipient.Chat(batch.Select(item => item.Payload).ToList()).ConfigureAwait(false);
                }

                MarkSuccessfulSend(recipient.Id, batch.SelectMany(item => item.Changes).ToList(), outboundAcknowledged: true);
            }
        }
        catch (BinaryChatAckRequiredGameException)
        {
            ResetPeerState(recipient.Id, fullResyncRequired: true);
        }
    }

    private void MarkSuccessfulSend(int recipientPlayerId, IReadOnlyList<IntelChange> appliedChanges, bool outboundAcknowledged)
    {
        lock (_peerStateSync)
        {
            var state = GetOrCreatePeerState(recipientPlayerId);
            state.HasSentBinaryToPeer = true;
            state.OutboundAcknowledged = outboundAcknowledged;
            state.AwaitingAcknowledgement = !outboundAcknowledged;

            foreach (var change in appliedChanges)
            {
                if (change.Kind == IntelChangeKind.Remove)
                {
                    state.LastSentSignaturesByUnit.Remove(change.UnitId);
                    continue;
                }

                if (change.Signature is not null)
                    state.LastSentSignaturesByUnit[change.UnitId] = change.Signature;
            }
        }
    }

    private void ResetPeerState(int recipientPlayerId, bool fullResyncRequired)
    {
        lock (_peerStateSync)
        {
            if (!_peerStates.TryGetValue(recipientPlayerId, out var state))
                return;

            state.OutboundAcknowledged = false;
            state.AwaitingAcknowledgement = false;
            state.HasSentBinaryToPeer = false;
            state.IncomingAcknowledgementInFlight = false;

            if (fullResyncRequired)
                state.LastSentSignaturesByUnit.Clear();
        }
    }

    private PeerState GetOrCreatePeerState(int playerId)
    {
        if (!_peerStates.TryGetValue(playerId, out var state))
        {
            state = new PeerState();
            _peerStates[playerId] = state;
        }

        return state;
    }

    private IEnumerable<Player> ResolveRemoteRecipients(SyncContext context)
    {
        var localPlayerIds = LocalTeamSessionRegistry.GetLocallyManagedPlayerIds(context.TeamScopeKey);
        foreach (var candidate in context.Galaxy.Players)
        {
            if (candidate is null ||
                candidate.Id == context.LocalPlayerId ||
                !string.Equals(candidate.Team.Name, context.TeamName, StringComparison.Ordinal) ||
                localPlayerIds.Contains(candidate.Id))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static bool IsFriendlyPeer(SyncContext context, Player player)
    {
        return player.Id != context.LocalPlayerId &&
               string.Equals(player.Team.Name, context.TeamName, StringComparison.Ordinal);
    }

    private List<UnitSnapshotDto> BuildShareableLocalUnits(SyncContext context)
    {
        var currentTick = _mappingService.TryGetCurrentTickForCurrentScope(out var tick) ? tick : 0u;
        var ownedUnits = new List<UnitSnapshotDto>();
        foreach (var controllable in context.Galaxy.Controllables)
        {
            var snapshot = TryMapOwnedControllable(context, controllable, currentTick);
            if (snapshot is null)
                continue;

            ownedUnits.Add(snapshot);
        }

        return MergeShareableUnits(_mappingService.BuildLocalGalaxyUnitSnapshots(), ownedUnits);
    }

    internal static List<UnitSnapshotDto> MergeShareableUnits(
        IReadOnlyList<UnitSnapshotDto> mappedUnits,
        IReadOnlyList<UnitSnapshotDto> ownedUnits)
    {
        var unitsById = new Dictionary<string, UnitSnapshotDto>(StringComparer.Ordinal);
        foreach (var unit in mappedUnits)
            unitsById[unit.UnitId] = unit;

        foreach (var unit in ownedUnits)
            unitsById[unit.UnitId] = unit;

        return unitsById.Values.ToList();
    }

    private static UnitSnapshotDto? TryMapOwnedControllable(SyncContext context, Controllable? controllable, uint currentTick)
    {
        if (controllable is null || !controllable.Alive || !controllable.Active)
            return null;

        return new UnitSnapshotDto
        {
            UnitId = UnitIdentity.BuildControllableId(context.LocalPlayerId, controllable.Id),
            ClusterId = controllable.Cluster?.Id ?? 0,
            Kind = MappingService.MapUnitKind(controllable.Kind),
            FullStateKnown = true,
            IsStatic = false,
            IsSolid = true,
            IsSeen = true,
            LastSeenTick = currentTick,
            X = controllable.Position.X,
            Y = controllable.Position.Y,
            MovementX = controllable.Movement.X,
            MovementY = controllable.Movement.Y,
            Angle = controllable.Angle,
            Radius = controllable.Size,
            Gravity = controllable.Gravity,
            SpeedLimit = controllable.SpeedLimit > 0f ? controllable.SpeedLimit : null,
            CurrentThrust = ResolveCurrentThrust(controllable),
            MaximumThrust = ResolveMaximumThrust(controllable),
            TeamName = context.TeamName
        };
    }

    private static float? ResolveCurrentThrust(Controllable controllable)
    {
        return controllable switch
        {
            ClassicShipControllable classic when classic.Engine.Exists => classic.Engine.Current.Length,
            ModernShipControllable modern => SumModernShipThrust(modern, current: true),
            _ => null
        };
    }

    private static float? ResolveMaximumThrust(Controllable controllable)
    {
        return controllable switch
        {
            ClassicShipControllable classic when classic.Engine.Exists && classic.Engine.Maximum > 0f => classic.Engine.Maximum,
            ModernShipControllable modern => SumModernShipThrust(modern, current: false),
            _ => null
        };
    }

    private static float? SumModernShipThrust(ModernShipControllable ship, bool current)
    {
        var total = 0f;
        var hasEngine = false;

        foreach (var engine in ship.Engines)
        {
            if (!engine.Exists)
                continue;

            hasEngine = true;
            total += current ? MathF.Abs(engine.CurrentThrust) : MathF.Max(0f, engine.MaximumThrust);
        }

        return hasEngine ? total : null;
    }

    private List<IntelChange> BuildPendingChanges(
        IReadOnlyList<UnitSnapshotDto> localUnits,
        IReadOnlyDictionary<string, string> lastSentSignaturesByUnit)
    {
        var changes = new List<IntelChange>();
        var currentSignaturesByUnit = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in localUnits)
        {
            var signature = CreateSignature(unit);
            currentSignaturesByUnit[unit.UnitId] = signature;

            if (lastSentSignaturesByUnit.TryGetValue(unit.UnitId, out var previousSignature) &&
                string.Equals(previousSignature, signature, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new IntelChange(IntelChangeKind.Upsert, unit.UnitId, unit, signature));
        }

        foreach (var previousUnitId in lastSentSignaturesByUnit.Keys)
        {
            if (currentSignaturesByUnit.ContainsKey(previousUnitId))
                continue;

            changes.Add(new IntelChange(IntelChangeKind.Remove, previousUnitId, null, null));
        }

        return changes;
    }

    private List<EncodedPayload> BuildEncodedPayloads(string galaxyId, IReadOnlyList<IntelChange> changes)
    {
        var payloads = new List<EncodedPayload>();
        var pending = new List<IntelChange>();

        for (var index = 0; index < changes.Count; index++)
        {
            var normalized = NormalizeForTransport(changes[index]);
            pending.Add(normalized);

            if (TryEncodeIntelPacket(galaxyId, pending, out var payload))
                continue;

            pending.RemoveAt(pending.Count - 1);
            if (pending.Count == 0)
            {
                _logger.LogWarning("Dropping oversized friendly-intel change for unit {UnitId}", changes[index].UnitId);
                continue;
            }

            if (!TryEncodeIntelPacket(galaxyId, pending, out payload))
                throw new InvalidOperationException("Failed to encode a previously validated friendly-intel payload.");

            payloads.Add(new EncodedPayload(payload, pending.ToList()));
            pending.Clear();
            index--;
        }

        if (pending.Count > 0)
        {
            if (!TryEncodeIntelPacket(galaxyId, pending, out var payload))
                throw new InvalidOperationException("Failed to encode trailing friendly-intel payload.");

            payloads.Add(new EncodedPayload(payload, pending.ToList()));
        }

        return payloads;
    }

    private IntelChange NormalizeForTransport(IntelChange change)
    {
        if (change.Kind != IntelChangeKind.Upsert || change.Snapshot is null)
            return change;

        var snapshot = CloneSyncSnapshot(change.Snapshot);
        snapshot.PredictedTrajectory = NormalizeTrajectory(snapshot.PredictedTrajectory);
        var normalized = new IntelChange(change.Kind, snapshot.UnitId, snapshot, CreateSignature(snapshot));
        if (TryEncodeIntelPacket(_contextResolver()?.GalaxyId ?? string.Empty, new[] { normalized }, out _))
            return normalized;

        snapshot.PredictedTrajectory = null;
        return new IntelChange(change.Kind, snapshot.UnitId, snapshot, CreateSignature(snapshot));
    }

    private async Task SendAcknowledgementAsync(Player recipient, int recipientPlayerId, string galaxyId, ulong acknowledgedMessageId)
    {
        try
        {
            await recipient.Chat(EncodeAcknowledgement(galaxyId, acknowledgedMessageId)).ConfigureAwait(false);

            lock (_peerStateSync)
            {
                var state = GetOrCreatePeerState(recipientPlayerId);
                state.HasSentBinaryToPeer = true;
                state.IncomingAcknowledgementInFlight = false;
            }
        }
        catch
        {
            lock (_peerStateSync)
            {
                if (_peerStates.TryGetValue(recipientPlayerId, out var state))
                    state.IncomingAcknowledgementInFlight = false;
            }

            throw;
        }
    }

    private ulong NextMessageId()
    {
        _nextMessageId++;
        return _nextMessageId;
    }

    private byte[] EncodeAcknowledgement(string galaxyId, ulong acknowledgedMessageId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteHeader(buffer, PacketKind.Acknowledgement, NextMessageId(), galaxyId);
        WriteUInt64(buffer, acknowledgedMessageId);
        return buffer.WrittenSpan.ToArray();
    }

    private bool TryEncodeIntelPacket(string galaxyId, IReadOnlyList<IntelChange> changes, out byte[] payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteHeader(buffer, PacketKind.IntelDelta, NextMessageId(), galaxyId);
        WriteUInt16(buffer, checked((ushort)changes.Count));

        for (var index = 0; index < changes.Count; index++)
            WriteChange(buffer, changes[index]);

        if (buffer.WrittenCount > MaximumSinglePayloadBytes)
        {
            payload = Array.Empty<byte>();
            return false;
        }

        payload = buffer.WrittenSpan.ToArray();
        return true;
    }

    private static void WriteHeader(ArrayBufferWriter<byte> buffer, PacketKind kind, ulong messageId, string galaxyId)
    {
        WriteBytes(buffer, Magic);
        WriteByte(buffer, ProtocolVersion);
        WriteByte(buffer, (byte)kind);
        WriteUInt64(buffer, messageId);
        WriteUInt64(buffer, ComputeGalaxyHash(galaxyId));
    }

    private static bool TryDecodePacket(string expectedGalaxyId, byte[] payload, out DecodedPacket packet)
    {
        packet = default;
        if (payload is null || payload.Length < 22)
            return false;

        var offset = 0;
        if (!payload.AsSpan(offset, Magic.Length).SequenceEqual(Magic))
            return false;

        offset += Magic.Length;
        if (payload[offset++] != ProtocolVersion)
            return false;

        var kind = (PacketKind)payload[offset++];
        if (!TryReadUInt64(payload, ref offset, out var messageId) ||
            !TryReadUInt64(payload, ref offset, out var galaxyHash) ||
            galaxyHash != ComputeGalaxyHash(expectedGalaxyId))
        {
            return false;
        }

        switch (kind)
        {
            case PacketKind.Acknowledgement:
                if (!TryReadUInt64(payload, ref offset, out var acknowledgedMessageId))
                    return false;

                packet = new DecodedPacket(kind, messageId, acknowledgedMessageId, new List<IntelChange>());
                return offset == payload.Length;

            case PacketKind.IntelDelta:
                if (!TryReadUInt16(payload, ref offset, out var changeCount))
                    return false;

                var changes = new List<IntelChange>(changeCount);
                for (var index = 0; index < changeCount; index++)
                {
                    if (!TryReadChange(payload, ref offset, out var change))
                        return false;

                    changes.Add(change);
                }

                if (offset != payload.Length)
                    return false;

                packet = new DecodedPacket(kind, messageId, 0, changes);
                return true;

            default:
                return false;
        }
    }

    private static void WriteChange(ArrayBufferWriter<byte> buffer, IntelChange change)
    {
        WriteByte(buffer, (byte)change.Kind);
        WriteString(buffer, change.UnitId);

        if (change.Kind == IntelChangeKind.Remove || change.Snapshot is null)
            return;

        var snapshotBytes = SerializeSnapshotBytes(CloneSyncSnapshot(change.Snapshot));
        WriteUInt16(buffer, checked((ushort)snapshotBytes.Length));
        WriteBytes(buffer, snapshotBytes);
    }

    private static bool TryReadChange(byte[] payload, ref int offset, out IntelChange change)
    {
        change = default;
        if (!TryReadByte(payload, ref offset, out var kindByte))
            return false;

        var kind = (IntelChangeKind)kindByte;
        if (!TryReadString(payload, ref offset, out var unitId))
            return false;

        if (kind == IntelChangeKind.Remove)
        {
            change = new IntelChange(kind, unitId, null, null);
            return true;
        }

        if (!TryReadUInt16(payload, ref offset, out var snapshotLength) ||
            offset + snapshotLength > payload.Length)
            return false;

        if (!TryDeserializeSnapshot(payload.AsSpan(offset, snapshotLength), out var snapshot) ||
            snapshot is null)
        {
            return false;
        }

        offset += snapshotLength;
        snapshot.UnitId = unitId;

        change = new IntelChange(kind, unitId, snapshot, CreateSignature(snapshot));
        return true;
    }

    private static string CreateSignature(UnitSnapshotDto snapshot)
    {
        return Convert.ToHexString(SHA256.HashData(SerializeSnapshotBytes(CloneSyncSnapshot(snapshot))));
    }

    private static UnitSnapshotDto CloneSyncSnapshot(UnitSnapshotDto snapshot)
    {
        return new UnitSnapshotDto
        {
            UnitId = snapshot.UnitId,
            ClusterId = snapshot.ClusterId,
            Kind = snapshot.Kind,
            FullStateKnown = snapshot.FullStateKnown,
            IsStatic = snapshot.IsStatic,
            IsSolid = snapshot.IsSolid,
            IsSeen = snapshot.IsSeen,
            LastSeenTick = snapshot.LastSeenTick,
            X = snapshot.X,
            Y = snapshot.Y,
            MovementX = snapshot.MovementX,
            MovementY = snapshot.MovementY,
            Angle = snapshot.Angle,
            Radius = snapshot.Radius,
            Gravity = snapshot.Gravity,
            SpeedLimit = snapshot.SpeedLimit,
            CurrentThrust = snapshot.CurrentThrust,
            MaximumThrust = snapshot.MaximumThrust,
            PredictedTrajectory = NormalizeTrajectory(snapshot.PredictedTrajectory),
            TeamName = snapshot.TeamName,
            ScannedSubsystems = CloneScannedSubsystems(snapshot.ScannedSubsystems),
            SunEnergy = snapshot.SunEnergy,
            SunIons = snapshot.SunIons,
            SunNeutrinos = snapshot.SunNeutrinos,
            SunHeat = snapshot.SunHeat,
            SunDrain = snapshot.SunDrain,
            PlanetMetal = snapshot.PlanetMetal,
            PlanetCarbon = snapshot.PlanetCarbon,
            PlanetHydrogen = snapshot.PlanetHydrogen,
            PlanetSilicon = snapshot.PlanetSilicon,
            MissionTargetSequenceNumber = snapshot.MissionTargetSequenceNumber,
            MissionTargetVectorCount = snapshot.MissionTargetVectorCount,
            MissionTargetVectors = CloneTrajectory(snapshot.MissionTargetVectors),
            FlagActive = snapshot.FlagActive,
            FlagGraceTicks = snapshot.FlagGraceTicks,
            DominationRadius = snapshot.DominationRadius,
            Domination = snapshot.Domination,
            DominationScoreCountdown = snapshot.DominationScoreCountdown,
            WormHoleTargetClusterName = snapshot.WormHoleTargetClusterName,
            WormHoleTargetLeft = snapshot.WormHoleTargetLeft,
            WormHoleTargetTop = snapshot.WormHoleTargetTop,
            WormHoleTargetRight = snapshot.WormHoleTargetRight,
            WormHoleTargetBottom = snapshot.WormHoleTargetBottom,
            CurrentFieldMode = snapshot.CurrentFieldMode,
            CurrentFieldFlowX = snapshot.CurrentFieldFlowX,
            CurrentFieldFlowY = snapshot.CurrentFieldFlowY,
            CurrentFieldRadialForce = snapshot.CurrentFieldRadialForce,
            CurrentFieldTangentialForce = snapshot.CurrentFieldTangentialForce,
            NebulaHue = snapshot.NebulaHue,
            StormSpawnChancePerTick = snapshot.StormSpawnChancePerTick,
            StormMinAnnouncementTicks = snapshot.StormMinAnnouncementTicks,
            StormMaxAnnouncementTicks = snapshot.StormMaxAnnouncementTicks,
            StormMinActiveTicks = snapshot.StormMinActiveTicks,
            StormMaxActiveTicks = snapshot.StormMaxActiveTicks,
            StormMinWhirlRadius = snapshot.StormMinWhirlRadius,
            StormMaxWhirlRadius = snapshot.StormMaxWhirlRadius,
            StormMinWhirlSpeed = snapshot.StormMinWhirlSpeed,
            StormMaxWhirlSpeed = snapshot.StormMaxWhirlSpeed,
            StormMinWhirlGravity = snapshot.StormMinWhirlGravity,
            StormMaxWhirlGravity = snapshot.StormMaxWhirlGravity,
            StormDamage = snapshot.StormDamage,
            StormWhirlRemainingTicks = snapshot.StormWhirlRemainingTicks,
            PowerUpAmount = snapshot.PowerUpAmount,
        };
    }

    private static List<TrajectoryPointDto>? NormalizeTrajectory(IReadOnlyList<TrajectoryPointDto>? trajectory)
    {
        if (trajectory is null || trajectory.Count == 0)
            return null;

        return trajectory
            .Take(byte.MaxValue)
            .Select(point => new TrajectoryPointDto { X = point.X, Y = point.Y })
            .ToList();
    }

    private static List<TrajectoryPointDto>? CloneTrajectory(IReadOnlyList<TrajectoryPointDto>? trajectory)
    {
        if (trajectory is null || trajectory.Count == 0)
            return null;

        return trajectory
            .Select(point => new TrajectoryPointDto { X = point.X, Y = point.Y })
            .ToList();
    }

    private static List<ScannedSubsystemDto>? CloneScannedSubsystems(IReadOnlyList<ScannedSubsystemDto>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        return source
            .Select(subsystem => new ScannedSubsystemDto
            {
                Id = subsystem.Id,
                Name = subsystem.Name,
                Exists = subsystem.Exists,
                Status = subsystem.Status,
                Stats = subsystem.Stats
                    .Select(stat => new ScannedSubsystemStatDto
                    {
                        Label = stat.Label,
                        Value = stat.Value
                    })
                    .ToList()
            })
            .ToList();
    }

    private static byte[] SerializeSnapshotBytes(UnitSnapshotDto snapshot)
    {
        var messagePack = MessagePackSerializer.Serialize(snapshot, SyncMessagePackOptions);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(messagePack, 0, messagePack.Length);

        return output.ToArray();
    }

    private static bool TryDeserializeSnapshot(ReadOnlySpan<byte> payload, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;

        try
        {
            using var input = new MemoryStream(payload.ToArray(), writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            snapshot = MessagePackSerializer.Deserialize<UnitSnapshotDto>(brotli, SyncMessagePackOptions);
            return snapshot is not null;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool TryMarkMessageProcessed(string scopeKey, int sourcePlayerId, ulong messageId)
    {
        var key = $"{sourcePlayerId}:{messageId}";
        lock (ProcessedMessageSync)
        {
            if (!ProcessedMessagesByScope.TryGetValue(scopeKey, out var journal))
            {
                journal = new ScopeProcessedMessages();
                ProcessedMessagesByScope[scopeKey] = journal;
            }

            if (!journal.Keys.Add(key))
                return false;

            journal.Order.Enqueue(key);
            while (journal.Order.Count > MaximumProcessedMessagesPerScope)
            {
                var removed = journal.Order.Dequeue();
                journal.Keys.Remove(removed);
            }

            return true;
        }
    }

    private static ulong ComputeGalaxyHash(string galaxyId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(galaxyId));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, sizeof(ulong)));
    }

    private static void WriteBytes(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> bytes)
    {
        var span = buffer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        buffer.Advance(bytes.Length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> buffer, byte value)
    {
        var span = buffer.GetSpan(sizeof(byte));
        span[0] = value;
        buffer.Advance(sizeof(byte));
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> buffer, ushort value)
    {
        var span = buffer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        buffer.Advance(sizeof(ushort));
    }

    private static void WriteUInt32(ArrayBufferWriter<byte> buffer, uint value)
    {
        var span = buffer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        buffer.Advance(sizeof(uint));
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> buffer, ulong value)
    {
        var span = buffer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        buffer.Advance(sizeof(ulong));
    }

    private static void WriteInt32(ArrayBufferWriter<byte> buffer, int value)
    {
        var span = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        buffer.Advance(sizeof(int));
    }

    private static void WriteSingle(ArrayBufferWriter<byte> buffer, float value)
    {
        WriteUInt32(buffer, BitConverter.SingleToUInt32Bits(value));
    }

    private static void WriteString(ArrayBufferWriter<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(buffer, checked((ushort)bytes.Length));
        WriteBytes(buffer, bytes);
    }

    private static void WriteIsSolid(ArrayBufferWriter<byte> buffer, bool? isSolid)
    {
        WriteByte(buffer, isSolid switch
        {
            true => 2,
            false => 1,
            _ => 0
        });
    }

    private static bool TryReadByte(byte[] payload, ref int offset, out byte value)
    {
        value = 0;
        if (offset >= payload.Length)
            return false;

        value = payload[offset++];
        return true;
    }

    private static bool TryReadUInt16(byte[] payload, ref int offset, out ushort value)
    {
        value = 0;
        if (offset + sizeof(ushort) > payload.Length)
            return false;

        value = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(byte[] payload, ref int offset, out uint value)
    {
        value = 0;
        if (offset + sizeof(uint) > payload.Length)
            return false;

        value = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadUInt64(byte[] payload, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + sizeof(ulong) > payload.Length)
            return false;

        value = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return true;
    }

    private static bool TryReadInt32(byte[] payload, ref int offset, out int value)
    {
        value = 0;
        if (offset + sizeof(int) > payload.Length)
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadSingle(byte[] payload, ref int offset, out float value)
    {
        value = 0f;
        if (!TryReadUInt32(payload, ref offset, out var bits))
            return false;

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    private static bool TryReadString(byte[] payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(payload, ref offset, out var length) ||
            offset + length > payload.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadIsSolid(byte[] payload, ref int offset, out bool? isSolid)
    {
        isSolid = null;
        if (!TryReadByte(payload, ref offset, out var value))
            return false;

        isSolid = value switch
        {
            2 => true,
            1 => false,
            _ => null
        };
        return true;
    }
}
