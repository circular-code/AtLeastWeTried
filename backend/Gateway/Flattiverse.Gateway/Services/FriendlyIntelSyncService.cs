using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Protocol.Dtos;
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

            var localUnits = _mappingService.BuildLocalGalaxyUnitSnapshots();
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

        WriteInt32(buffer, change.Snapshot.ClusterId);
        WriteString(buffer, change.Snapshot.Kind);
        WriteByte(buffer, BuildFieldFlags(change.Snapshot));
        WriteByte(buffer, BuildPresenceFlags(change.Snapshot));
        WriteUInt32(buffer, change.Snapshot.LastSeenTick);
        WriteSingle(buffer, change.Snapshot.X);
        WriteSingle(buffer, change.Snapshot.Y);
        WriteSingle(buffer, change.Snapshot.Angle);
        WriteSingle(buffer, change.Snapshot.Radius);
        WriteSingle(buffer, change.Snapshot.Gravity);
        WriteIsSolid(buffer, change.Snapshot.IsSolid);

        if (change.Snapshot.MovementX.HasValue && change.Snapshot.MovementY.HasValue)
        {
            WriteSingle(buffer, change.Snapshot.MovementX.Value);
            WriteSingle(buffer, change.Snapshot.MovementY.Value);
        }

        if (change.Snapshot.SpeedLimit.HasValue)
            WriteSingle(buffer, change.Snapshot.SpeedLimit.Value);

        if (change.Snapshot.CurrentThrust.HasValue)
            WriteSingle(buffer, change.Snapshot.CurrentThrust.Value);

        if (change.Snapshot.MaximumThrust.HasValue)
            WriteSingle(buffer, change.Snapshot.MaximumThrust.Value);

        if (!string.IsNullOrWhiteSpace(change.Snapshot.TeamName))
            WriteString(buffer, change.Snapshot.TeamName!);

        var trajectory = NormalizeTrajectory(change.Snapshot.PredictedTrajectory);
        WriteByte(buffer, checked((byte)(trajectory?.Count ?? 0)));
        if (trajectory is null)
            return;

        for (var index = 0; index < trajectory.Count; index++)
        {
            WriteSingle(buffer, trajectory[index].X);
            WriteSingle(buffer, trajectory[index].Y);
        }
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

        if (!TryReadInt32(payload, ref offset, out var clusterId) ||
            !TryReadString(payload, ref offset, out var unitKind) ||
            !TryReadByte(payload, ref offset, out var fieldFlags) ||
            !TryReadByte(payload, ref offset, out var presenceFlags) ||
            !TryReadUInt32(payload, ref offset, out var lastSeenTick) ||
            !TryReadSingle(payload, ref offset, out var x) ||
            !TryReadSingle(payload, ref offset, out var y) ||
            !TryReadSingle(payload, ref offset, out var angle) ||
            !TryReadSingle(payload, ref offset, out var radius) ||
            !TryReadSingle(payload, ref offset, out var gravity) ||
            !TryReadIsSolid(payload, ref offset, out var isSolid))
        {
            return false;
        }

        float? movementX = null;
        float? movementY = null;
        if ((presenceFlags & 0x01) != 0)
        {
            if (!TryReadSingle(payload, ref offset, out var readMovementX) ||
                !TryReadSingle(payload, ref offset, out var readMovementY))
            {
                return false;
            }

            movementX = readMovementX;
            movementY = readMovementY;
        }

        float? speedLimit = null;
        if ((presenceFlags & 0x02) != 0)
        {
            if (!TryReadSingle(payload, ref offset, out var readSpeedLimit))
                return false;

            speedLimit = readSpeedLimit;
        }

        float? currentThrust = null;
        if ((presenceFlags & 0x04) != 0)
        {
            if (!TryReadSingle(payload, ref offset, out var readCurrentThrust))
                return false;

            currentThrust = readCurrentThrust;
        }

        float? maximumThrust = null;
        if ((presenceFlags & 0x08) != 0)
        {
            if (!TryReadSingle(payload, ref offset, out var readMaximumThrust))
                return false;

            maximumThrust = readMaximumThrust;
        }

        string? teamName = null;
        if ((presenceFlags & 0x10) != 0)
        {
            if (!TryReadString(payload, ref offset, out var readTeamName))
                return false;

            teamName = readTeamName;
        }

        if (!TryReadByte(payload, ref offset, out var trajectoryCount))
            return false;

        List<TrajectoryPointDto>? trajectory = null;
        if (trajectoryCount > 0)
        {
            trajectory = new List<TrajectoryPointDto>(trajectoryCount);
            for (var index = 0; index < trajectoryCount; index++)
            {
                if (!TryReadSingle(payload, ref offset, out var pointX) ||
                    !TryReadSingle(payload, ref offset, out var pointY))
                {
                    return false;
                }

                trajectory.Add(new TrajectoryPointDto { X = pointX, Y = pointY });
            }
        }

        var snapshot = new UnitSnapshotDto
        {
            UnitId = unitId,
            ClusterId = clusterId,
            Kind = unitKind,
            FullStateKnown = (fieldFlags & 0x01) != 0,
            IsStatic = (fieldFlags & 0x02) != 0,
            IsSeen = (fieldFlags & 0x04) != 0,
            LastSeenTick = lastSeenTick,
            X = x,
            Y = y,
            MovementX = movementX,
            MovementY = movementY,
            Angle = angle,
            Radius = radius,
            Gravity = gravity,
            IsSolid = isSolid,
            SpeedLimit = speedLimit,
            CurrentThrust = currentThrust,
            MaximumThrust = maximumThrust,
            TeamName = teamName,
            PredictedTrajectory = trajectory
        };

        change = new IntelChange(kind, unitId, snapshot, CreateSignature(snapshot));
        return true;
    }

    private static byte BuildFieldFlags(UnitSnapshotDto snapshot)
    {
        byte flags = 0;
        if (snapshot.FullStateKnown)
            flags |= 0x01;
        if (snapshot.IsStatic)
            flags |= 0x02;
        if (snapshot.IsSeen)
            flags |= 0x04;
        return flags;
    }

    private static byte BuildPresenceFlags(UnitSnapshotDto snapshot)
    {
        byte flags = 0;
        if (snapshot.MovementX.HasValue && snapshot.MovementY.HasValue)
            flags |= 0x01;
        if (snapshot.SpeedLimit.HasValue)
            flags |= 0x02;
        if (snapshot.CurrentThrust.HasValue)
            flags |= 0x04;
        if (snapshot.MaximumThrust.HasValue)
            flags |= 0x08;
        if (!string.IsNullOrWhiteSpace(snapshot.TeamName))
            flags |= 0x10;
        return flags;
    }

    private static string CreateSignature(UnitSnapshotDto snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteChange(buffer, new IntelChange(IntelChangeKind.Upsert, snapshot.UnitId, CloneSyncSnapshot(snapshot), null));
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
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
            TeamName = snapshot.TeamName,
            PredictedTrajectory = NormalizeTrajectory(snapshot.PredictedTrajectory)
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
