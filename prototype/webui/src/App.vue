<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import WorldViewport from './components/WorldViewport.vue';
import { createGatewayClient, type ClientConnectionState } from './lib/gateway';
import { loadSavedConnections, maskApiKey, removeSavedConnection, upsertSavedConnection, type SavedGatewayConnection } from './lib/savedConnections';
import { readNavigationTarget, type WorldSceneSelection } from './lib/worldScene';
import type {
  ChatEntryDto,
  CommandReplyMessage,
  GalaxySnapshotDto,
  OwnerOverlayDeltaMessage,
  PlayerSessionSummaryDto,
  ServerMessage,
  ServerStatusMessage,
  SessionReadyMessage,
  ShipClass,
  WorldDeltaMessage,
} from './types/generated';

type QueuedAttachment = {
  apiKey: string;
  teamName: string;
};

type OverlayBadgeTone = 'accent' | 'ok' | 'muted' | 'warn';

type OverlayBadge = {
  label: string;
  tone: OverlayBadgeTone;
};

type OverlayMeterTone = 'energy' | 'hull' | 'shield';

type OverlayMeter = {
  label: string;
  ratio: number;
  valueText: string;
  tone: OverlayMeterTone;
  inactive?: boolean;
};

type OverlayStat = {
  label: string;
  value: string;
};

type OverlayDetailGroup = {
  title: string;
  tone: 'solar' | 'hazard';
  stats: OverlayStat[];
};

type OverlayEntry = {
  id: string;
  displayName: string;
  kind: string;
  alive: boolean;
  clusterLabel: string;
  badges: OverlayBadge[];
  statusLabel?: string;
  meters: OverlayMeter[];
  stats: OverlayStat[];
};

type ClickedUnitEntry = OverlayEntry & {
  positionLabel: string;
  detailGroups: OverlayDetailGroup[];
};

type ActivityTone = 'info' | 'success' | 'warn' | 'error';

type ActivityEntry = {
  id: string;
  tone: ActivityTone;
  summary: string;
  detail?: string;
  meta?: string;
  createdAt: number;
};

type PendingCommandDescriptor = {
  label: string;
  subject?: string;
};

const gatewayUrl = import.meta.env.VITE_GATEWAY_URL ?? 'ws://127.0.0.1:5260/ws';
const connectionState = ref<ClientConnectionState>('idle');
const session = ref<SessionReadyMessage | null>(null);
const snapshot = ref<GalaxySnapshotDto | null>(null);
const ownerOverlay = ref<Record<string, unknown>>({});
const chatEntries = ref<ChatEntryDto[]>([]);
const activityEntries = ref<ActivityEntry[]>([]);
const selectedPlayerSessionId = ref('');
const selectedControllableId = ref('');
const chatDraft = ref('');
const shipName = ref('Aurora Wing');
const thrust = ref(0.25);
const subsystemMode = ref('pinpoint');
const apiKeyDraft = ref('');
const teamNameDraft = ref('');
const rememberKey = ref(true);
const isManagerPopupOpen = ref(false);
const isChatPopupOpen = ref(false);
const savedConnections = ref<SavedGatewayConnection[]>(loadSavedConnections());
const pendingAttachments = ref<QueuedAttachment[]>([]);
const lastSelection = ref<WorldSceneSelection | null>(null);
const pendingCommands = ref<Record<string, PendingCommandDescriptor>>({});
const isActivityHistoryOpen = ref(false);
const playerApiKeyPattern = /^[0-9a-f]{64}$/i;
const recentActivityLimit = 3;
const activityHistoryLimit = 48;
const activityToastLifetimeMs = 8000;
const activityClock = ref(Date.now());
let activityClockHandle: number | undefined;

const client = createGatewayClient(gatewayUrl, {
  onStateChange(nextState) {
    connectionState.value = nextState;

    if (nextState === 'open') {
      flushPendingAttachments();
    }
  },
  onMessage(message) {
    if (message.type === 'ping') {
      client.send({ type: 'pong' });
      return;
    }

    applyMessage(message);
  },
});

const activePlayer = computed<PlayerSessionSummaryDto | null>(() => {
  if (!session.value) {
    return null;
  }

  return session.value.playerSessions.find((player) => player.selected) ?? null;
});

const attachedSessions = computed(() => session.value?.playerSessions ?? []);

const activeControllableId = computed(() => {
  if (selectedControllableId.value) {
    return selectedControllableId.value;
  }

  const overlayKeys = Object.keys(ownerOverlay.value);
  if (overlayKeys.length > 0) {
    return overlayKeys[0] ?? '';
  }

  if (!snapshot.value || !activePlayer.value) {
    return '';
  }

  return snapshot.value.controllables.find((controllable) => controllable.displayName === activePlayer.value?.displayName)?.controllableId ?? '';
});

const ownedControllables = computed(() => {
  return Object.entries(ownerOverlay.value).map(([controllableId, overlay]) => {
    const publicControllable = snapshot.value?.controllables.find((item) => item.controllableId === controllableId);
    const overlayState = typeof overlay === 'object' && overlay !== null ? (overlay as Record<string, unknown>) : {};

    return {
      controllableId,
      displayName: publicControllable?.displayName ?? stringValue(overlayState.displayName, controllableId),
      teamName: publicControllable?.teamName ?? activePlayer.value?.teamName ?? 'Unknown',
      alive: getControllableAliveState(publicControllable, overlayState),
      score: publicControllable?.score ?? 0,
      kind: stringValue(overlayState.kind, 'unknown'),
    };
  });
});

const worldStats = computed(() => {
  return {
    teams: snapshot.value?.teams.length ?? 0,
    clusters: snapshot.value?.clusters.length ?? 0,
    units: snapshot.value?.units.length ?? 0,
    controllables: snapshot.value?.controllables.length ?? 0,
  };
});

const activeControllable = computed(() => {
  return ownedControllables.value.find((controllable) => controllable.controllableId === activeControllableId.value) ?? null;
});

const activeNavigationTarget = computed(() => {
  if (!activeControllableId.value) {
    return null;
  }

  return readNavigationTarget(ownerOverlay.value, activeControllableId.value);
});

const selectedSessionSummary = computed(() => {
  return attachedSessions.value.find((player) => player.playerSessionId === selectedPlayerSessionId.value) ?? null;
});

const connectionStateLabel = computed(() => {
  switch (connectionState.value) {
    case 'open': return 'Connected';
    case 'connecting': return 'Connecting…';
    case 'closed': return 'Disconnected';
    case 'error': return 'Connection error';
    default: return 'Idle';
  }
});

const showDisconnectAction = computed(() => {
  return connectionState.value === 'open' || connectionState.value === 'connecting';
});

const floatingActivityEntries = computed(() => activityEntries.value
  .filter((entry) => activityClock.value - entry.createdAt < activityToastLifetimeMs)
  .slice(0, recentActivityLimit));

const olderActivityCount = computed(() => Math.max(0, activityEntries.value.length - floatingActivityEntries.value.length));

const latestChatEntry = computed(() => chatEntries.value[0] ?? null);

const ownerOverlayEntries = computed<OverlayEntry[]>(() => {
  return Object.entries(ownerOverlay.value).map(([id, data]) => {
    const overlayState = objectValue(data) ?? {};
    const publicControllable = snapshot.value?.controllables.find((item) => item.controllableId === id);
    const positionState = objectValue(overlayState.position);
    const movementState = objectValue(overlayState.movement);
    const engineState = objectValue(overlayState.engine);
    const scannerState = objectValue(overlayState.scanner);
    const shieldState = objectValue(overlayState.shield);
    const alive = getControllableAliveState(publicControllable, overlayState);
    const active = booleanValue(overlayState.active, false);
    const scannerActive = booleanValue(scannerState?.active, false);
    const engineMax = numberValue(engineState?.maximum, 0);
    const engineLoad = engineMax > 0
      ? magnitude(numberValue(engineState?.currentX, 0), numberValue(engineState?.currentY, 0)) / engineMax
      : 0;
    const speed = magnitude(numberValue(movementState?.x, 0), numberValue(movementState?.y, 0));
    const clusterName = stringValue(overlayState.clusterName, 'Unknown cluster');
    const clusterId = numberValue(overlayState.clusterId, 0);

    const badges: OverlayBadge[] = [];
    if (id === activeControllableId.value) {
      badges.push({ label: 'selected', tone: 'accent' });
    }

    const statusLabel = !alive
      ? 'Destroyed'
      : scannerActive
        ? 'Scanner online'
        : 'Scanner offline';

    return {
      id,
      displayName: publicControllable?.displayName ?? stringValue(overlayState.displayName, id),
      kind: stringValue(overlayState.kind, 'unknown'),
      alive,
      clusterLabel: `${clusterName} · C${clusterId}`,
      badges,
      statusLabel,
      meters: [
        buildOverlayMeter('Hull', objectValue(overlayState.hull), 'hull', alive ? 'offline' : 'destroyed'),
        buildOverlayMeter(
          'Shield',
          shieldState,
          'shield',
          booleanValue(shieldState?.active, false) ? 'charging' : 'offline',
        ),
        buildOverlayMeter('Battery', objectValue(overlayState.energyBattery), 'energy', 'offline'),
      ],
      stats: [
        { label: 'Ammo', value: formatWholeValue(overlayState.ammo) },
        { label: 'Speed', value: formatMetric(speed) },
        { label: 'Heading', value: formatAngle(numberValue(positionState?.angle, 0)) },
        { label: 'Drive', value: engineMax > 0 ? `${Math.round(clamp01(engineLoad) * 100)}%` : 'idle' },
      ],
    };
  });
});

const clickedUnitEntry = computed<ClickedUnitEntry | null>(() => {
  const selection = lastSelection.value;
  const unitId = selection?.unitId;
  if (!selection || !unitId) {
    return null;
  }

  const publicUnit = snapshot.value?.units.find((item) => item.unitId === unitId);
  const publicControllable = snapshot.value?.controllables.find((item) => item.controllableId === unitId);
  const overlayState = objectValue(ownerOverlay.value[unitId]) ?? {};
  const positionState = objectValue(overlayState.position);
  const movementState = objectValue(overlayState.movement);
  const engineState = objectValue(overlayState.engine);
  const scannerState = objectValue(overlayState.scanner);
  const shieldState = objectValue(overlayState.shield);
  const hullState = objectValue(overlayState.hull);
  const batteryState = objectValue(overlayState.energyBattery);
  const clusterId = publicUnit?.clusterId ?? numberValue(overlayState.clusterId, 0);
  const clusterName = snapshot.value?.clusters.find((cluster) => cluster.id === clusterId)?.name
    ?? stringValue(overlayState.clusterName, clusterId > 0 ? `Cluster ${clusterId}` : 'Unknown cluster');
  const teamName = publicControllable?.teamName ?? publicUnit?.teamName ?? optionalStringValue(overlayState.teamName);
  const alive = getControllableAliveState(publicControllable, overlayState);
  const active = booleanValue(overlayState.active, false);
  const scannerActive = booleanValue(scannerState?.active, false);
  const speed = magnitude(numberValue(movementState?.x, 0), numberValue(movementState?.y, 0));
  const engineMax = numberValue(engineState?.maximum, 0);
  const engineLoad = engineMax > 0
    ? magnitude(numberValue(engineState?.currentX, 0), numberValue(engineState?.currentY, 0)) / engineMax
    : 0;
  const unitX = publicUnit?.x ?? numberValue(positionState?.x, selection.worldX);
  const unitY = publicUnit?.y ?? numberValue(positionState?.y, -selection.worldY);
  const angle = publicUnit?.angle ?? numberValue(positionState?.angle, 0);
  const kind = publicUnit?.kind ?? stringValue(overlayState.kind, selection.kind ?? 'unknown');
  const sunEnergy = optionalNumberValue(publicUnit?.sunEnergy);
  const sunIons = optionalNumberValue(publicUnit?.sunIons);
  const sunNeutrinos = optionalNumberValue(publicUnit?.sunNeutrinos);
  const sunHeat = optionalNumberValue(publicUnit?.sunHeat);
  const sunDrain = optionalNumberValue(publicUnit?.sunDrain);
  const badges: OverlayBadge[] = [];

  if (publicControllable) {
    badges.push({ label: 'controllable', tone: 'accent' });
  }

  if (hasRecordKey(overlayState, 'active')) {
    badges.push({ label: active ? 'active' : 'idle', tone: active ? 'ok' : 'muted' });
  }

  if (publicControllable || hasRecordKey(overlayState, 'alive')) {
    badges.push({ label: alive ? 'alive' : 'down', tone: alive ? 'ok' : 'warn' });
  }

  if (hasRecordKey(overlayState, 'scanner') || scannerActive) {
    badges.push({ label: scannerActive ? 'scan' : 'scan off', tone: scannerActive ? 'accent' : 'muted' });
  }

  const stats: OverlayStat[] = [
    { label: 'Team', value: teamName ?? selection.teamName ?? 'Neutral' },
    { label: 'Cluster', value: clusterId > 0 ? `${clusterName} · C${clusterId}` : clusterName },
    { label: 'Position', value: `${formatMetric(unitX)}, ${formatMetric(unitY)}` },
    { label: 'Heading', value: formatAngle(angle) },
    { label: 'Speed', value: formatMetric(speed) },
  ];

  if (publicControllable) {
    stats.push({ label: 'Score', value: formatMetric(publicControllable.score) });
  }

  if (hasRecordKey(overlayState, 'ammo')) {
    stats.push({ label: 'Ammo', value: formatWholeValue(overlayState.ammo) });
  }

  if (engineMax > 0) {
    stats.push({ label: 'Drive', value: `${Math.round(clamp01(engineLoad) * 100)}%` });
  }

  const meters: OverlayMeter[] = [];
  if (hullState || publicControllable) {
    meters.push(buildOverlayMeter('Hull', hullState, 'hull', alive ? 'offline' : 'destroyed'));
  }
  if (shieldState || publicControllable) {
    meters.push(
      buildOverlayMeter(
        'Shield',
        shieldState,
        'shield',
        booleanValue(shieldState?.active, false) ? 'charging' : 'offline',
      ),
    );
  }
  if (batteryState || publicControllable) {
    meters.push(buildOverlayMeter('Battery', batteryState, 'energy', 'offline'));
  }

  const detailGroups: OverlayDetailGroup[] = [];
  if (kind === 'sun' && hasSunTelemetry(publicUnit)) {
    detailGroups.push({
      title: 'Stellar Output',
      tone: 'solar',
      stats: [
        { label: 'Photon Flux', value: formatMetric(sunEnergy ?? 0) },
        { label: 'Plasma Wind', value: formatMetric(sunIons ?? 0) },
        { label: 'Neutrino Flux', value: formatMetric(sunNeutrinos ?? 0) },
      ],
    });
    detailGroups.push({
      title: 'Environmental Hazard',
      tone: 'hazard',
      stats: [
        { label: 'Heat', value: `${formatMetric(sunHeat ?? 0)} · ${formatMetric((sunHeat ?? 0) * 15)} energy/tick` },
        { label: 'Radiation', value: `${formatMetric(sunDrain ?? 0)} · ${formatMetric((sunDrain ?? 0) * 0.125)} hull/tick` },
      ],
    });
  }

  return {
    id: unitId,
    displayName: publicControllable?.displayName ?? stringValue(overlayState.displayName, unitId),
    kind,
    alive,
    clusterLabel: clusterId > 0 ? `${clusterName} · C${clusterId}` : clusterName,
    positionLabel: `${formatMetric(unitX)}, ${formatMetric(unitY)}`,
    badges,
    meters,
    stats,
    detailGroups,
  };
});

function connect() {
  if (savedConnections.value.length > 0 && pendingAttachments.value.length === 0) {
    pendingAttachments.value = savedConnections.value
      .filter((entry) => isValidPlayerApiKey(entry.apiKey))
      .map((entry) => ({
        apiKey: entry.apiKey,
        teamName: entry.teamName,
      }));
  }

  client.connect();
}

function openManagerPopup() {
  isManagerPopupOpen.value = true;
}

function closeManagerPopup() {
  isManagerPopupOpen.value = false;
}

function openChatPopup() {
  isChatPopupOpen.value = true;
}

function closeChatPopup() {
  isChatPopupOpen.value = false;
}

function openActivityHistory() {
  isActivityHistoryOpen.value = true;
}

function closeActivityHistory() {
  isActivityHistoryOpen.value = false;
}

function disconnect() {
  client.disconnect();
}

function addConnection() {
  const apiKey = apiKeyDraft.value.trim();
  if (!apiKey) {
    return;
  }

  if (!isValidPlayerApiKey(apiKey)) {
    recordActivity({
      tone: 'warn',
      summary: 'Invalid Player API key',
      detail: 'Enter a 64-character hexadecimal Flattiverse Player API key.',
      meta: 'local validation',
    });
    return;
  }

  if (rememberKey.value) {
    savedConnections.value = upsertSavedConnection(apiKey, teamNameDraft.value);
  }

  queueAttachment(apiKey, teamNameDraft.value);
  apiKeyDraft.value = '';
}

function attachSavedConnection(savedConnection: SavedGatewayConnection) {
  if (!isValidPlayerApiKey(savedConnection.apiKey)) {
    recordActivity({
      tone: 'warn',
      summary: `Saved key '${savedConnection.label}' is invalid`,
      detail: 'Saved Player API keys must be 64-character hexadecimal values.',
      meta: 'local validation',
    });
    return;
  }

  queueAttachment(savedConnection.apiKey, savedConnection.teamName);
}

function detachPlayerSession(playerSessionId: string) {
  if (!playerSessionId) {
    return;
  }

  client.detachPlayerSession(playerSessionId);
}

function forgetSavedConnection(id: string) {
  savedConnections.value = removeSavedConnection(id);
}

function queueAttachment(apiKey: string, teamName: string) {
  if (!isValidPlayerApiKey(apiKey)) {
    recordActivity({
      tone: 'warn',
      summary: 'Invalid Player API key',
      detail: 'Enter a 64-character hexadecimal Flattiverse Player API key.',
      meta: 'local validation',
    });
    return;
  }

  pendingAttachments.value = [
    ...pendingAttachments.value,
    {
      apiKey: apiKey.trim(),
      teamName: teamName.trim(),
    },
  ];

  if (connectionState.value === 'open') {
    flushPendingAttachments();
    return;
  }

  client.connect();
}

function flushPendingAttachments() {
  if (connectionState.value !== 'open' || pendingAttachments.value.length === 0) {
    return;
  }

  const seenKeys = new Set<string>();
  for (const attachment of pendingAttachments.value) {
    const normalizedKey = attachment.apiKey.trim().toLowerCase();
    if (!normalizedKey || seenKeys.has(normalizedKey)) {
      continue;
    }

    seenKeys.add(normalizedKey);
    client.attachConnection(attachment.apiKey.trim(), attachment.teamName.trim() || undefined);
  }

  pendingAttachments.value = [];
}

function selectPlayer() {
  if (!selectedPlayerSessionId.value) {
    return;
  }

  ownerOverlay.value = {};
  selectedControllableId.value = '';
  client.selectPlayerSession(selectedPlayerSessionId.value);
}

function selectControllable(controllableId?: string) {
  if (controllableId) {
    selectedControllableId.value = controllableId;
    return;
  }

  if (!selectedControllableId.value && ownedControllables.value.length > 0) {
    selectedControllableId.value = ownedControllables.value[0]?.controllableId ?? '';
  }
}

function sendChat() {
  const message = chatDraft.value.trim();
  if (!message) {
    return;
  }

  const commandId = client.sendChat(message, 'galaxy');
  trackCommand(commandId, 'Transmit galaxy chat', truncateText(message, 72));
  chatDraft.value = '';
}

function createShip(shipClass: ShipClass = 'modern') {
  const shipLabel = shipName.value.trim() || 'Prototype Wing';
  const commandId = client.createShip(shipLabel, shipClass, []);
  trackCommand(commandId, `Deploy ${shipClass} ship`, shipLabel);
}

function destroyShip(controllableId?: string) {
  const targetControllableId = controllableId ?? activeControllableId.value;
  if (!targetControllableId) {
    return;
  }

  if (controllableId) {
    selectedControllableId.value = controllableId;
  }

  const commandId = client.destroyShip(targetControllableId);
  trackCommand(commandId, 'Destroy ship', getControllableLabel(targetControllableId));
}

function continueShip(controllableId?: string) {
  const targetControllableId = controllableId ?? activeControllableId.value;
  if (!targetControllableId) {
    return;
  }

  if (controllableId) {
    selectedControllableId.value = controllableId;
  }

  const commandId = client.continueShip(targetControllableId);
  trackCommand(commandId, 'Spawn ship', getControllableLabel(targetControllableId));
}

function removeShip(controllableId?: string) {
  const targetControllableId = controllableId ?? activeControllableId.value;
  if (!targetControllableId) {
    return;
  }

  if (controllableId) {
    selectedControllableId.value = controllableId;
  }

  const commandId = client.removeShip(targetControllableId);
  trackCommand(commandId, 'Remove ship', getControllableLabel(targetControllableId));
}

function updateEngine() {
  if (!activeControllableId.value) {
    return;
  }

  const commandId = client.setEngine(activeControllableId.value, 'main_engine', thrust.value);
  trackCommand(commandId, `Set thrust to ${thrust.value.toFixed(2)}`, getControllableLabel(activeControllableId.value));
}

function fireWeapon() {
  if (!activeControllableId.value) {
    return;
  }

  const commandId = client.fireWeapon(activeControllableId.value, 'main_weapon', 0);
  trackCommand(commandId, 'Fire weapon', getControllableLabel(activeControllableId.value));
}

function applySubsystemMode() {
  if (!activeControllableId.value) {
    return;
  }

  const commandId = client.setSubsystemMode(activeControllableId.value, 'primary_scanner', subsystemMode.value);
  trackCommand(commandId, `Set subsystem mode to ${subsystemMode.value}`, getControllableLabel(activeControllableId.value));
}

function handleWorldSelect(selection: WorldSceneSelection) {
  lastSelection.value = selection;
}

function handleWorldNavigate(selection: WorldSceneSelection) {
  if (!activeControllableId.value) {
    recordActivity({
      tone: 'warn',
      summary: 'No active ship selected',
      detail: 'Select one of your controllables before setting a navigation target.',
      meta: 'navigation target',
    });
    return;
  }

  const commandId = client.setNavigationTarget(activeControllableId.value, selection.worldX, -selection.worldY);
  trackCommand(
    commandId,
    'Set navigation target',
    `${getControllableLabel(activeControllableId.value)} -> ${formatMetric(selection.worldX)}, ${formatMetric(-selection.worldY)}`,
  );
}

function clearNavigationTarget() {
  if (!activeControllableId.value) {
    return;
  }

  clearNavigationOverlay(activeControllableId.value);
  const commandId = client.clearNavigationTarget(activeControllableId.value);
  trackCommand(commandId, 'Clear navigation target', getControllableLabel(activeControllableId.value));
}

function handleWindowKeydown(event: KeyboardEvent) {
  if (isEditableTarget(event.target)) {
    return;
  }

  if (event.key.toLowerCase() === 's') {
    event.preventDefault();
    clearNavigationTarget();
    return;
  }

  if (event.key !== 'Escape') {
    return;
  }

  if (isActivityHistoryOpen.value) {
    closeActivityHistory();
    return;
  }

  if (isChatPopupOpen.value) {
    closeChatPopup();
    return;
  }

  if (isManagerPopupOpen.value) {
    closeManagerPopup();
  }
}

function applyMessage(message: ServerMessage) {
  switch (message.type) {
    case 'session.ready':
      session.value = message;
      selectedPlayerSessionId.value = message.playerSessions.find((player) => player.selected)?.playerSessionId ?? '';
      ownerOverlay.value = {};
      selectedControllableId.value = '';
      break;
    case 'snapshot.full':
      snapshot.value = cloneSnapshot(message.snapshot);
      break;
    case 'world.delta':
      if (snapshot.value) {
        snapshot.value = applyWorldDelta(snapshot.value, message);
      }
      break;
    case 'owner.delta':
      ownerOverlay.value = applyOwnerOverlay(ownerOverlay.value, message);
      break;
    case 'chat.received':
      chatEntries.value = [message.entry, ...chatEntries.value].slice(0, 12);
      break;
    case 'status':
      recordActivity(createStatusActivity(message));
      break;
    case 'command.reply':
      if (message.status === 'completed') {
        const action = optionalStringValue(message.result?.action);
        const controllableId = optionalStringValue(message.result?.controllableId);
        if (controllableId && action !== 'remove_requested') {
          selectedControllableId.value = controllableId;
        }
      }

      recordActivity(createCommandReplyActivity(message));
      break;
  }
}

function cloneSnapshot(source: GalaxySnapshotDto): GalaxySnapshotDto {
  return {
    ...source,
    teams: source.teams.map((team) => ({ ...team })),
    clusters: source.clusters.map((cluster) => ({ ...cluster })),
    units: source.units.map((unit) => ({ ...unit })),
    controllables: source.controllables.map((controllable) => ({ ...controllable })),
  };
}

function applyWorldDelta(current: GalaxySnapshotDto, message: WorldDeltaMessage): GalaxySnapshotDto {
  const next = cloneSnapshot(current);

  for (const event of message.events) {
    if (event.eventType === 'unit.updated') {
      const unit = next.units.find((item) => item.unitId === event.entityId);
      if (!unit || !event.changes) {
        continue;
      }

      unit.clusterId = numberValue(event.changes.clusterId, unit.clusterId);
      unit.kind = stringValue(event.changes.kind, unit.kind);
      unit.x = numberValue(event.changes.x, unit.x);
      unit.y = numberValue(event.changes.y, unit.y);
      unit.angle = numberValue(event.changes.angle, unit.angle);
      unit.radius = numberValue(event.changes.radius, unit.radius);
      unit.teamName = hasRecordKey(event.changes, 'teamName') ? optionalStringValue(event.changes.teamName) : unit.teamName;
      applyOptionalUnitMetric(unit, 'sunEnergy', event.changes.sunEnergy);
      applyOptionalUnitMetric(unit, 'sunIons', event.changes.sunIons);
      applyOptionalUnitMetric(unit, 'sunNeutrinos', event.changes.sunNeutrinos);
      applyOptionalUnitMetric(unit, 'sunHeat', event.changes.sunHeat);
      applyOptionalUnitMetric(unit, 'sunDrain', event.changes.sunDrain);
      continue;
    }

    if (event.eventType === 'unit.created' && event.changes) {
      next.units = [
        ...next.units,
        {
          unitId: event.entityId,
          clusterId: numberValue(event.changes.clusterId, 1),
          kind: stringValue(event.changes.kind, 'unknown'),
          x: numberValue(event.changes.x, 0),
          y: numberValue(event.changes.y, 0),
          angle: numberValue(event.changes.angle, 0),
          radius: numberValue(event.changes.radius, 3),
          teamName: optionalStringValue(event.changes.teamName),
          sunEnergy: optionalNumberValue(event.changes.sunEnergy),
          sunIons: optionalNumberValue(event.changes.sunIons),
          sunNeutrinos: optionalNumberValue(event.changes.sunNeutrinos),
          sunHeat: optionalNumberValue(event.changes.sunHeat),
          sunDrain: optionalNumberValue(event.changes.sunDrain),
        },
      ];
      continue;
    }

    if (event.eventType === 'unit.removed') {
      next.units = next.units.filter((item) => item.unitId !== event.entityId);
      continue;
    }

    if (event.eventType === 'controllable.created' && event.changes) {
      next.controllables = [
        ...next.controllables,
        {
          controllableId: event.entityId,
          displayName: stringValue(event.changes.displayName, 'Unnamed'),
          teamName: stringValue(event.changes.teamName, 'Unknown'),
          alive: booleanValue(event.changes.alive, true),
          score: numberValue(event.changes.score, 0),
        },
      ];
    }
  }

  return next;
}

function applyOwnerOverlay(current: Record<string, unknown>, message: OwnerOverlayDeltaMessage) {
  const next: Record<string, unknown> = message.events.some((event) => event.eventType === 'overlay.snapshot') ? {} : { ...current };

  for (const event of message.events) {
    if (!event.changes) {
      delete next[event.controllableId];
      continue;
    }

    next[event.controllableId] = {
      ...(event.eventType !== 'overlay.snapshot' && typeof next[event.controllableId] === 'object' && next[event.controllableId] !== null
        ? (next[event.controllableId] as Record<string, unknown>)
        : {}),
      ...event.changes,
    };
  }

  return next;
}

function clearNavigationOverlay(controllableId: string) {
  const currentOverlay = objectValue(ownerOverlay.value[controllableId]);
  if (!currentOverlay || !hasRecordKey(currentOverlay, 'navigation')) {
    return;
  }

  const nextOverlay = { ...currentOverlay };
  delete nextOverlay.navigation;

  ownerOverlay.value = {
    ...ownerOverlay.value,
    [controllableId]: nextOverlay,
  };
}

function trackCommand(commandId: string, label: string, subject?: string) {
  pendingCommands.value = {
    ...pendingCommands.value,
    [commandId]: {
      label,
      subject,
    },
  };
}

function consumeCommand(commandId: string) {
  const descriptor = pendingCommands.value[commandId];
  if (!descriptor) {
    return undefined;
  }

  const { [commandId]: _removed, ...rest } = pendingCommands.value;
  pendingCommands.value = rest;
  return descriptor;
}

function createStatusActivity(message: ServerStatusMessage): ActivityEntry {
  return {
    tone: statusKindToTone(message.kind),
    summary: humanizeCode(message.code),
    detail: message.message,
    meta: buildStatusMeta(message),
    id: `status-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    createdAt: Date.now(),
  };
}

function createCommandReplyActivity(message: CommandReplyMessage): ActivityEntry {
  const descriptor = consumeCommand(message.commandId);
  const summaryBase = descriptor?.subject ? `${descriptor.label} · ${descriptor.subject}` : descriptor?.label ?? humanizeCode(message.commandId);

  if (message.error) {
    return {
      id: `command-${message.commandId}`,
      tone: 'error',
      summary: `${summaryBase} failed`,
      detail: message.error.message,
      meta: message.error.code,
      createdAt: Date.now(),
    };
  }

  if (message.status === 'completed') {
    return {
      id: `command-${message.commandId}`,
      tone: 'success',
      summary: `${summaryBase} completed`,
      detail: formatCommandResult(message),
      createdAt: Date.now(),
    };
  }

  return {
    id: `command-${message.commandId}`,
    tone: message.status === 'accepted' ? 'info' : 'warn',
    summary: `${summaryBase} ${message.status}`,
    detail: formatCommandResult(message),
    createdAt: Date.now(),
  };
}

function formatCommandResult(message: CommandReplyMessage) {
  if (message.error) {
    return message.error.message;
  }

  const action = optionalStringValue(message.result?.action);
  const controllableId = optionalStringValue(message.result?.controllableId);
  const mode = optionalStringValue(message.result?.mode);
  const scope = optionalStringValue(message.result?.scope);
  const thrustValue = typeof message.result?.thrust === 'number' ? message.result.thrust : undefined;

  if (action === 'remove_requested') {
    return 'The ship will be removed by the upstream session when the request is processed.';
  }

  if (action === 'destroyed' && controllableId) {
    return `${getControllableLabel(controllableId)} self-destructed.`;
  }

  if (action === 'continued' && controllableId) {
    return `${getControllableLabel(controllableId)} resumed operations.`;
  }

  if (mode) {
    return `Mode set to ${mode}.`;
  }

  if (scope) {
    return `Message routed to ${scope} chat.`;
  }

  if (thrustValue !== undefined) {
    return `Requested thrust ${thrustValue.toFixed(2)}.`;
  }

  if (controllableId) {
    return `Target: ${getControllableLabel(controllableId)}.`;
  }

  return undefined;
}

function recordActivity(entry: Omit<ActivityEntry, 'id' | 'createdAt'> & Partial<Pick<ActivityEntry, 'id' | 'createdAt'>>) {
  activityEntries.value = [
    {
      id: entry.id ?? `activity-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      createdAt: entry.createdAt ?? Date.now(),
      tone: entry.tone,
      summary: entry.summary,
      detail: entry.detail,
      meta: entry.meta,
    },
    ...activityEntries.value,
  ].slice(0, activityHistoryLimit);
}

function statusKindToTone(kind: ServerStatusMessage['kind']): ActivityTone {
  switch (kind) {
    case 'error':
      return 'error';
    case 'warning':
      return 'warn';
    default:
      return 'info';
  }
}

function buildStatusMeta(message: ServerStatusMessage) {
  return message.recoverable ? `${message.kind} · recoverable` : `${message.kind} · requires attention`;
}

function humanizeCode(value: string) {
  return value
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/(^|\s)\S/g, (match) => match.toUpperCase());
}

function formatActivityTime(createdAt: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
  }).format(createdAt);
}

function truncateText(value: string, maxLength: number) {
  if (value.length <= maxLength) {
    return value;
  }

  return `${value.slice(0, maxLength - 1)}…`;
}

function getControllableLabel(controllableId: string) {
  const owned = ownedControllables.value.find((entry) => entry.controllableId === controllableId);
  if (owned) {
    return owned.displayName;
  }

  const publicControllable = snapshot.value?.controllables.find((entry) => entry.controllableId === controllableId);
  return publicControllable?.displayName ?? controllableId;
}

function isValidPlayerApiKey(apiKey: string) {
  return playerApiKeyPattern.test(apiKey.trim());
}

function numberValue(value: unknown, fallback: number) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function stringValue(value: unknown, fallback: string) {
  return typeof value === 'string' && value.length > 0 ? value : fallback;
}

function optionalStringValue(value: unknown) {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function optionalNumberValue(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function booleanValue(value: unknown, fallback: boolean) {
  return typeof value === 'boolean' ? value : fallback;
}

function objectValue(value: unknown) {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : undefined;
}

function hasRecordKey(record: Record<string, unknown>, key: string) {
  return Object.prototype.hasOwnProperty.call(record, key);
}

function getControllableAliveState(
  publicControllable: GalaxySnapshotDto['controllables'][number] | null | undefined,
  overlayState: Record<string, unknown>,
) {
  if (hasRecordKey(overlayState, 'alive')) {
    return booleanValue(overlayState.alive, publicControllable?.alive ?? true);
  }

  return publicControllable?.alive ?? true;
}

function hasSunTelemetry(unit: GalaxySnapshotDto['units'][number] | null | undefined) {
  return !!unit && [unit.sunEnergy, unit.sunIons, unit.sunNeutrinos, unit.sunHeat, unit.sunDrain].some((value) => typeof value === 'number');
}

function applyOptionalUnitMetric(
  unit: GalaxySnapshotDto['units'][number],
  key: 'sunEnergy' | 'sunIons' | 'sunNeutrinos' | 'sunHeat' | 'sunDrain',
  value: unknown,
) {
  if (value === undefined) {
    return;
  }

  unit[key] = optionalNumberValue(value);
}

function clamp01(value: number) {
  return Math.max(0, Math.min(1, value));
}

function ownerOverlayGaugeColor(tone: OverlayMeterTone) {
  switch (tone) {
    case 'hull':
      return '#ef9b72';
    case 'shield':
      return '#7abcf8';
    case 'energy':
    default:
      return '#86e6d8';
  }
}

function getOwnerOverlayGaugeStyle(meter: OverlayMeter) {
  const ratio = clamp01(meter.ratio);

  return {
    '--gauge-ratio': `${ratio > 0 ? Math.max(ratio, 0.03) : 0}`,
    '--gauge-fill': ownerOverlayGaugeColor(meter.tone),
  };
}

function magnitude(x: number, y: number) {
  return Math.hypot(x, y);
}

function buildOverlayMeter(
  label: string,
  state: Record<string, unknown> | undefined,
  tone: OverlayMeterTone,
  emptyLabel: string,
): OverlayMeter {
  const current = numberValue(state?.current, 0);
  const maximum = numberValue(state?.maximum, 0);

  return {
    label,
    ratio: maximum > 0 ? clamp01(current / maximum) : 0,
    valueText: maximum > 0 ? `${formatMetric(current)} / ${formatMetric(maximum)}` : emptyLabel,
    tone,
    inactive: maximum <= 0,
  };
}

function formatMetric(value: number) {
  const absolute = Math.abs(value);

  if (absolute >= 1000) {
    const scaled = value / 1000;
    return `${scaled.toFixed(Math.abs(scaled) >= 10 ? 0 : 1).replace(/\.0$/, '')}k`;
  }

  if (absolute >= 100) {
    return value.toFixed(0);
  }

  if (absolute >= 10) {
    return value.toFixed(1).replace(/\.0$/, '');
  }

  return value.toFixed(2).replace(/\.00$/, '').replace(/(\.\d)0$/, '$1');
}

function formatWholeValue(value: unknown) {
  return formatMetric(numberValue(value, 0));
}

function formatAngle(angle: number) {
  const normalized = ((angle % 360) + 360) % 360;
  return `${Math.round(normalized)}°`;
}

function isEditableTarget(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  const tagName = target.tagName.toLowerCase();
  return target.isContentEditable || tagName === 'input' || tagName === 'textarea' || tagName === 'select';
}

watch(
  attachedSessions,
  (nextSessions) => {
    if (nextSessions.length === 0) {
      selectedPlayerSessionId.value = '';
      return;
    }

    if (nextSessions.some((player) => player.playerSessionId === selectedPlayerSessionId.value)) {
      return;
    }

    selectedPlayerSessionId.value = nextSessions.find((player) => player.selected)?.playerSessionId ?? nextSessions[0]?.playerSessionId ?? '';
  },
  { immediate: true },
);

watch(
  ownedControllables,
  (nextControllables) => {
    if (nextControllables.length === 0) {
      selectedControllableId.value = '';
      return;
    }

    if (nextControllables.some((controllable) => controllable.controllableId === selectedControllableId.value)) {
      return;
    }

    selectedControllableId.value = nextControllables[0]?.controllableId ?? '';
  },
  { immediate: true },
);

onMounted(() => {
  if (savedConnections.value.length > 0) {
    connect();
  }

  activityClockHandle = window.setInterval(() => {
    activityClock.value = Date.now();
  }, 1000);
  window.addEventListener('keydown', handleWindowKeydown);
});

onBeforeUnmount(() => {
  if (activityClockHandle !== undefined) {
    window.clearInterval(activityClockHandle);
  }

  window.removeEventListener('keydown', handleWindowKeydown);
  client.disconnect();
});
</script>

<template>
  <main class="app-shell">
    <section class="scene-stage">
      <WorldViewport
        :snapshot="snapshot"
        :owner-overlay="ownerOverlay"
        :selected-controllable-id="activeControllableId"
        :navigation-target="activeNavigationTarget"
        @world-select="handleWorldSelect"
        @world-navigate="handleWorldNavigate"
      />

      <div class="overlay-shell">
        <div class="overlay-top">
          <section class="overlay-column overlay-column-left">
            <div class="owner-overlay-panel">
              <p v-if="ownerOverlayEntries.length === 0" class="text-muted">No overlay data.</p>
              <article
                v-for="entry in ownerOverlayEntries"
                :key="entry.id"
                :class="['owner-overlay-item', { 'is-selected': entry.id === activeControllableId }]"
                role="button"
                tabindex="0"
                @click="selectControllable(entry.id)"
                @keydown.enter.prevent="selectControllable(entry.id)"
                @keydown.space.prevent="selectControllable(entry.id)"
              >
                <div class="owner-overlay-summary">
                  <div class="owner-overlay-head">
                    <div class="owner-overlay-title">
                      <h3>{{ entry.displayName }}</h3>
                      <p>{{ entry.kind }}<span v-if="entry.statusLabel"> · {{ entry.statusLabel }}</span></p>
                    </div>
                    <div v-if="entry.badges.length > 0" class="overlay-badges owner-overlay-badges">
                      <span v-for="badge in entry.badges" :key="badge.label" :class="['overlay-badge', `is-${badge.tone}`]">{{ badge.label }}</span>
                    </div>
                  </div>
                  <div class="owner-overlay-meta">
                    <span>{{ entry.clusterLabel }}</span>
                    <span class="overlay-card-id">{{ entry.id }}</span>
                  </div>
                </div>
                <div class="owner-overlay-gauges">
                  <div v-for="meter in entry.meters" :key="meter.label" :class="['owner-overlay-gauge-shell', `tone-${meter.tone}`, { 'is-inactive': meter.inactive }]">
                    <div class="owner-overlay-gauge" :style="getOwnerOverlayGaugeStyle(meter)">
                      <div class="owner-overlay-gauge-core">
                        <strong>{{ meter.inactive ? 'off' : `${Math.round(clamp01(meter.ratio) * 100)}%` }}</strong>
                        <span>{{ meter.label }}</span>
                      </div>
                    </div>
                    <small>{{ meter.valueText }}</small>
                  </div>
                </div>
                <dl class="owner-overlay-stats">
                  <div v-for="stat in entry.stats" :key="stat.label">
                    <dt>{{ stat.label }}</dt>
                    <dd>{{ stat.value }}</dd>
                  </div>
                </dl>
                <div class="actions actions-compact owner-overlay-actions">
                  <button
                    v-if="!entry.alive"
                    class="primary button-compact"
                    type="button"
                    :disabled="!entry.id"
                    @click.stop="continueShip(entry.id)"
                  >
                    Spawn
                  </button>
                  <button
                    v-if="entry.alive"
                    class="danger button-compact"
                    type="button"
                    :disabled="!entry.id"
                    @click.stop="destroyShip(entry.id)"
                  >
                    Kill
                  </button>
                  <button
                    v-if="!entry.alive"
                    class="danger-subtle button-compact"
                    type="button"
                    :disabled="!entry.id"
                    @click.stop="removeShip(entry.id)"
                  >
                    Remove
                  </button>
                </div>
              </article>

              <article class="owner-overlay-create panel panel-glass">
                <label class="field owner-overlay-create-name">
                  <input v-model="shipName" type="text" placeholder="Ship name" @keydown.enter="() => createShip()" />
                </label>
                <div class="field owner-overlay-create-class">
                  <div class="owner-overlay-create-types">
                    <button
                      class="secondary button-compact"
                      type="button"
                      @click="createShip('modern')"
                    >
                      Modern
                    </button>
                    <button
                      class="secondary button-compact"
                      type="button"
                      @click="createShip('classic')"
                    >
                      Classic
                    </button>
                  </div>
                </div>
              </article>
            </div>
          </section>

          <section class="overlay-column overlay-column-right">
            <p v-if="!clickedUnitEntry" class="text-muted selection-empty-state">Click a unit to inspect it. Right-click with an active ship selected to set a navigation target.</p>
            <div v-else class="overlay-card selection-unit-card">
              <div class="overlay-card-head">
                <div class="overlay-card-title">
                  <h3>{{ clickedUnitEntry.displayName }}</h3>
                  <p>{{ clickedUnitEntry.kind }}</p>
                </div>
                <div v-if="clickedUnitEntry.badges.length > 0" class="overlay-badges">
                  <span v-for="badge in clickedUnitEntry.badges" :key="badge.label" :class="['overlay-badge', `is-${badge.tone}`]">{{ badge.label }}</span>
                </div>
              </div>
              <div class="overlay-card-meta">
                <span>{{ clickedUnitEntry.clusterLabel }}</span>
                <span class="overlay-card-id">{{ clickedUnitEntry.id }}</span>
              </div>
              <div class="selection-unit-position">Map position {{ clickedUnitEntry.positionLabel }}</div>
              <dl class="overlay-stat-grid selection-unit-grid">
                <div v-for="stat in clickedUnitEntry.stats" :key="stat.label">
                  <dt>{{ stat.label }}</dt>
                  <dd>{{ stat.value }}</dd>
                </div>
              </dl>
              <div v-if="clickedUnitEntry.meters.length > 0" class="overlay-meter-list">
                <div v-for="meter in clickedUnitEntry.meters" :key="meter.label" :class="['overlay-meter', `tone-${meter.tone}`, { 'is-inactive': meter.inactive }]">
                  <div class="overlay-meter-head">
                    <span>{{ meter.label }}</span>
                    <strong>{{ meter.valueText }}</strong>
                  </div>
                  <div class="overlay-meter-track">
                    <div class="overlay-meter-fill" :style="{ width: `${Math.round(meter.ratio * 100)}%` }"></div>
                  </div>
                </div>
              </div>
              <div v-if="clickedUnitEntry.detailGroups.length > 0" class="selection-detail-groups">
                <section v-for="group in clickedUnitEntry.detailGroups" :key="group.title" :class="['selection-detail-group', `tone-${group.tone}`]">
                  <header>
                    <h4>{{ group.title }}</h4>
                  </header>
                  <dl class="overlay-stat-grid selection-detail-grid">
                    <div v-for="stat in group.stats" :key="`${group.title}-${stat.label}`">
                      <dt>{{ stat.label }}</dt>
                      <dd>{{ stat.value }}</dd>
                    </div>
                  </dl>
                </section>
              </div>
            </div>
          </section>
        </div>

        <section class="activity-tray" aria-label="Recent activity">
          <ul class="activity-list compact-list">
            <li v-if="floatingActivityEntries.length === 0" class="activity-item activity-item-empty text-muted">No recent activity.</li>
            <li
              v-for="entry in floatingActivityEntries"
              :key="entry.id"
              :class="['activity-item', 'activity-item-toast', `tone-${entry.tone}`]"
            >
              <div class="activity-item-head">
                <strong>{{ entry.summary }}</strong>
                <span>{{ formatActivityTime(entry.createdAt) }}</span>
              </div>
            </li>
          </ul>
        </section>

        <section class="command-dock">
          <article class="panel panel-glass">
            <h2>Tactical</h2>
            <label class="field">
              <span>Thrust {{ thrust.toFixed(2) }}</span>
              <input v-model="thrust" type="range" min="-1" max="1" step="0.05" />
            </label>
            <div class="actions">
              <button class="primary full" type="button" @click="updateEngine">Apply Thrust</button>
              <button class="primary full" type="button" @click="fireWeapon">Fire Weapon</button>
            </div>
          </article>

          <article class="panel panel-glass">
            <h2>Subsystem</h2>
            <label class="field">
              <span>Mode</span>
              <input v-model="subsystemMode" type="text" placeholder="pinpoint" />
            </label>
            <button class="primary full" type="button" @click="applySubsystemMode">Apply Mode</button>
          </article>

        </section>

        <section class="overlay-status-bar" aria-label="World status bar">
          <div class="status-bar-group status-bar-group-left">
            <div :class="['status-bar-item', 'status-bar-item-primary', `is-${connectionState}`]">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <circle cx="8" cy="8" r="3.25" fill="currentColor"></circle>
                  <path d="M8 1.75v2.1M8 12.15v2.1M1.75 8h2.1M12.15 8h2.1" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4"></path>
                </svg>
              </span>
              <span class="status-bar-text">{{ connectionStateLabel }}</span>
            </div>

            <div class="status-bar-item">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <circle cx="8" cy="5.1" r="2.25" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
                  <path d="M3.1 13.15c.75-2.2 2.55-3.3 4.9-3.3s4.15 1.1 4.9 3.3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4"></path>
                </svg>
              </span>
              <span class="status-bar-text">{{ activePlayer?.displayName ?? 'Observer Mode' }}</span>
            </div>

            <div class="status-bar-item">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <path d="M2.2 12.4h11.6M4.1 10.8V4.25M8 10.8V2.7M11.9 10.8V5.8" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4"></path>
                </svg>
              </span>
              <span class="status-bar-text">{{ activeControllable?.displayName ?? 'No Active Ship' }}</span>
            </div>

            <div class="status-bar-item status-bar-actions">
              <button
                v-if="showDisconnectAction"
                class="secondary button-compact status-bar-button"
                type="button"
                @click="disconnect"
              >
                Disconnect
              </button>
              <button
                v-else
                class="primary button-compact status-bar-button"
                type="button"
                @click="connect"
              >
                Connect
              </button>
              <button
                class="secondary button-compact status-bar-button"
                type="button"
                @click="openManagerPopup"
              >
                Players
              </button>
            </div>

          </div>

          <div class="status-bar-group status-bar-group-center">
            <div class="status-bar-item status-bar-item-metric" title="Teams in snapshot">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <circle cx="5" cy="5.25" r="1.55" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
                  <circle cx="11" cy="5.25" r="1.55" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
                  <path d="M2.7 12.8c.45-1.9 1.7-2.9 3.35-2.9 1.2 0 2.2.45 2.95 1.35M7.4 12.8c.45-1.9 1.7-2.9 3.35-2.9 1.7 0 2.95 1 3.4 2.9" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
                </svg>
              </span>
              <span class="status-bar-label">Teams</span>
              <strong>{{ worldStats.teams }}</strong>
            </div>

            <div class="status-bar-item status-bar-item-metric" title="Clusters in snapshot">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <circle cx="4.2" cy="8" r="1.55" fill="none" stroke="currentColor" stroke-width="1.3"></circle>
                  <circle cx="8" cy="4.2" r="1.55" fill="none" stroke="currentColor" stroke-width="1.3"></circle>
                  <circle cx="11.8" cy="8" r="1.55" fill="none" stroke="currentColor" stroke-width="1.3"></circle>
                  <circle cx="8" cy="11.8" r="1.55" fill="none" stroke="currentColor" stroke-width="1.3"></circle>
                  <path d="M5.35 6.85 6.85 5.35M9.15 5.35l1.5 1.5M10.65 9.15l-1.5 1.5M6.85 10.65l-1.5-1.5" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.15"></path>
                </svg>
              </span>
              <span class="status-bar-label">Clusters</span>
              <strong>{{ worldStats.clusters }}</strong>
            </div>

            <div class="status-bar-item status-bar-item-metric" title="Units in snapshot">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <path d="M8 2.2 13.3 5v6L8 13.8 2.7 11V5z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.3"></path>
                  <path d="M8 2.2V13.8M2.7 5 8 8l5.3-3" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.1"></path>
                </svg>
              </span>
              <span class="status-bar-label">Units</span>
              <strong>{{ worldStats.units }}</strong>
            </div>

            <div class="status-bar-item status-bar-item-metric" title="Public controllables in snapshot">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <path d="M8 2.25 11.1 5.8 8 13.75 4.9 5.8z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.3"></path>
                  <path d="M6.2 6.15h3.6" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
                </svg>
              </span>
              <span class="status-bar-label">Ships</span>
              <strong>{{ worldStats.controllables }}</strong>
            </div>

            <div class="status-bar-item status-bar-item-metric" title="Attached player sessions">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <rect x="2.2" y="3" width="11.6" height="10" rx="1.8" fill="none" stroke="currentColor" stroke-width="1.3"></rect>
                  <path d="M5 6.2h6M5 8.25h6M5 10.3h3.2" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
                </svg>
              </span>
              <span class="status-bar-label">Sessions</span>
              <strong>{{ attachedSessions.length }}</strong>
            </div>
          </div>

          <div class="status-bar-group status-bar-group-right">
            <div class="status-bar-item status-bar-item-chat-preview" :title="latestChatEntry ? `${latestChatEntry.senderDisplayName}: ${latestChatEntry.message}` : 'No messages yet'">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <path d="M3 3.25h10a1 1 0 0 1 1 1v5.5a1 1 0 0 1-1 1H7.3L4.6 13.4v-2.65H3a1 1 0 0 1-1-1v-5.5a1 1 0 0 1 1-1Z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.2"></path>
                </svg>
              </span>
              <div class="status-bar-chat-copy">
                <template v-if="latestChatEntry">
                  <strong class="status-bar-chat-sender">{{ latestChatEntry.senderDisplayName }}</strong>
                  <span class="status-bar-chat-message">{{ latestChatEntry.message }}</span>
                </template>
                <span v-else class="status-bar-chat-message">No messages yet.</span>
              </div>
            </div>

            <div class="status-bar-item status-bar-item-chat-launch">
              <span class="status-bar-icon" aria-hidden="true">
                <svg viewBox="0 0 16 16" focusable="false">
                  <path d="M2.5 13.2 13.75 8 2.5 2.8l1.65 4.15L9 8l-4.85 1.05z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.2"></path>
                </svg>
              </span>
              <button class="secondary button-compact status-bar-button" type="button" @click="openChatPopup">Open Chat</button>
            </div>

            <div class="status-bar-item status-bar-item-history">
              <button
                class="secondary button-compact status-bar-button status-bar-icon-button"
                type="button"
                :disabled="activityEntries.length === 0"
                :title="olderActivityCount > 0 ? `Older Messages (${olderActivityCount})` : 'Message History'"
                :aria-label="olderActivityCount > 0 ? `Open message history with ${olderActivityCount} older messages` : 'Open message history'"
                @click="openActivityHistory"
              >
                <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
                  <path d="M8 2.25a3.25 3.25 0 0 0-3.25 3.25v1.35c0 .56-.2 1.1-.56 1.53L3 9.75h10l-1.19-1.37a2.3 2.3 0 0 1-.56-1.53V5.5A3.25 3.25 0 0 0 8 2.25Z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.2"></path>
                  <path d="M6.4 11.35a1.73 1.73 0 0 0 3.2 0" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
                </svg>
                <span v-if="olderActivityCount > 0" class="status-bar-icon-badge">{{ olderActivityCount }}</span>
              </button>
            </div>
          </div>
        </section>

        <div v-if="isActivityHistoryOpen" class="modal-backdrop" @click.self="closeActivityHistory">
          <section class="modal-panel panel panel-glass activity-history-panel" aria-label="Activity history">
            <div class="modal-header">
              <div>
                <p class="eyebrow">Gateway Activity</p>
                <h1>Message History</h1>
              </div>
              <button class="secondary" type="button" @click="closeActivityHistory">Close</button>
            </div>

            <ul class="activity-list activity-history-list">
              <li v-if="activityEntries.length === 0" class="activity-item activity-item-empty text-muted">No activity yet.</li>
              <li
                v-for="entry in activityEntries"
                :key="entry.id"
                :class="['activity-item', 'activity-item-history', `tone-${entry.tone}`]"
              >
                <div class="activity-item-head">
                  <strong>{{ entry.summary }}</strong>
                  <span>{{ formatActivityTime(entry.createdAt) }}</span>
                </div>
                <p v-if="entry.detail">{{ entry.detail }}</p>
                <small v-if="entry.meta">{{ entry.meta }}</small>
              </li>
            </ul>
          </section>
        </div>

        <div v-if="isChatPopupOpen" class="modal-backdrop" @click.self="closeChatPopup">
          <section class="modal-panel panel panel-glass chat-modal-panel" aria-label="Galaxy chat">
            <div class="modal-header">
              <div>
                <p class="eyebrow">Communication</p>
                <h1>Galaxy Chat</h1>
              </div>
              <button class="secondary" type="button" @click="closeChatPopup">Close</button>
            </div>

            <ul class="message-list compact-list chat-modal-list">
              <li v-if="chatEntries.length === 0" class="text-muted">No messages yet.</li>
              <li v-for="entry in chatEntries" :key="entry.messageId">
                <strong>{{ entry.senderDisplayName }}</strong>
                <span>{{ entry.message }}</span>
              </li>
            </ul>

            <form class="chat-compose" @submit.prevent="sendChat">
              <input v-model="chatDraft" type="text" placeholder="Transmit a message…" aria-label="Transmit a message" />
              <button class="primary" type="submit">Send</button>
            </form>
          </section>
        </div>

        <div v-if="isManagerPopupOpen" class="modal-backdrop" @click.self="closeManagerPopup">
          <section class="modal-panel panel panel-glass" aria-label="Player and session management">
            <div class="modal-header">
              <div>
                <p class="eyebrow">Player Operations</p>
                <h1>Player &amp; Session Management</h1>
              </div>
              <button class="secondary" type="button" @click="closeManagerPopup">Close</button>
            </div>

            <div class="modal-grid">
              <section class="modal-column">
                <article class="panel panel-glass panel-scroll modal-section-card">
                  <h2>Attached Sessions</h2>
                  <label class="field">
                    <span>Selected Session</span>
                    <select v-model="selectedPlayerSessionId" :disabled="attachedSessions.length === 0" @change="selectPlayer">
                      <option v-if="attachedSessions.length === 0" value="">No attached sessions</option>
                      <option v-for="player in attachedSessions" :key="player.playerSessionId" :value="player.playerSessionId">
                        {{ player.displayName }} · {{ player.teamName ?? 'No team' }}
                      </option>
                    </select>
                  </label>
                  <ul class="message-list compact-list">
                    <li v-if="attachedSessions.length === 0">No attached sessions.</li>
                    <li v-for="player in attachedSessions" :key="player.playerSessionId">
                      <strong>{{ player.displayName }}</strong>
                      <span>{{ player.teamName ?? 'No team' }} · {{ player.connected ? 'connected' : 'unavailable' }}</span>
                      <button class="secondary" type="button" @click="detachPlayerSession(player.playerSessionId)">Detach</button>
                    </li>
                  </ul>
                </article>

                <article class="panel panel-glass panel-scroll modal-section-card">
                  <h2>Saved Player Keys</h2>
                  <ul class="message-list compact-list">
                    <li v-if="savedConnections.length === 0">No saved Player API keys.</li>
                    <li v-for="savedConnection in savedConnections" :key="savedConnection.id">
                      <strong>{{ savedConnection.label }}</strong>
                      <span>{{ maskApiKey(savedConnection.apiKey) }}<template v-if="savedConnection.teamName"> · {{ savedConnection.teamName }}</template></span>
                      <div class="actions actions-tight">
                        <button class="secondary" type="button" @click="attachSavedConnection(savedConnection)">Attach</button>
                        <button class="secondary" type="button" @click="forgetSavedConnection(savedConnection.id)">Forget</button>
                      </div>
                    </li>
                  </ul>
                </article>
              </section>

              <section class="modal-column">
                <article class="panel panel-glass modal-section-card">
                  <h2>Attach Player Session</h2>
                  <label class="field">
                    <span>Player API Key</span>
                    <input v-model="apiKeyDraft" type="password" autocomplete="off" placeholder="64-character hexadecimal Player API key" />
                  </label>
                  <label class="field">
                    <span>Team Hint</span>
                    <input v-model="teamNameDraft" type="text" placeholder="Optional team name" />
                  </label>
                  <label class="field field-inline">
                    <span>Remember Locally</span>
                    <input v-model="rememberKey" type="checkbox" />
                  </label>
                  <button class="primary full" type="button" @click="addConnection">Attach Player Session</button>
                </article>

                <article class="panel panel-glass modal-section-card">
                  <h2>Connection</h2>
                  <dl>
                    <div>
                      <dt>Gateway</dt>
                      <dd>{{ gatewayUrl }}</dd>
                    </div>
                    <div>
                      <dt>Pending Attachments</dt>
                      <dd>{{ pendingAttachments.length }}</dd>
                    </div>
                  </dl>
                  <div class="actions">
                    <button v-if="showDisconnectAction" class="secondary" type="button" @click="disconnect">Disconnect</button>
                    <button v-else class="primary" type="button" @click="connect">Connect</button>
                  </div>
                </article>
              </section>
            </div>
          </section>
        </div>
      </div>
    </section>
  </main>
</template>