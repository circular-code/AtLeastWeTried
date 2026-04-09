import { defineStore } from 'pinia';
import {
  buildStatusMeta,
  clamp01,
  formatAngle,
  formatCommandReplyDetail,
  formatMetric,
  formatWholeValue,
  humanizeCode,
  magnitude,
  statusKindToTone,
} from '../lib/formatting';
import {
  booleanValue,
  hasRecordKey,
  numberValue,
  objectValue,
  optionalNumberValue,
  optionalStringValue,
  stringValue,
} from '../lib/validation';
import type { WorldSceneSelection } from '../renderer/WorldScene';
import type {
  ChatEntryDto,
  ClusterSnapshotDto,
  CommandReplyMessage,
  GalaxySnapshotDto,
  OwnerOverlayDeltaMessage,
  PlayerSessionSummaryDto,
  PublicControllableSnapshotDto,
  ServerStatusMessage,
  SnapshotMessage,
  TeamSnapshotDto,
  UnitSnapshotDto,
  WorldDeltaMessage,
} from '../types/generated';
import type {
  ActivityEntry,
  ClickedUnitEntry,
  ControllableOverlayState,
  OverlayEntry,
  OverlayMeter,
  OverlayMeterTone,
  OverlayStat,
  OwnedControllableSummary,
  PendingCommandDescriptor,
  ScannerMode,
} from '../types/client';
import { useSessionStore } from './session';

type GameState = {
  galaxy: GalaxySnapshotDto | null;
  unitsById: Map<string, UnitSnapshotDto>;
  controllablesById: Map<string, PublicControllableSnapshotDto>;
  teamsById: Map<number, TeamSnapshotDto>;
  clustersById: Map<number, ClusterSnapshotDto>;
  overlayBySessionId: Map<string, Map<string, ControllableOverlayState>>;
  chatEntries: ChatEntryDto[];
  pendingCommands: Map<string, PendingCommandDescriptor>;
  activityEntries: ActivityEntry[];
};

const activityHistoryLimit = 48;

export const useGameStore = defineStore('game', {
  state: (): GameState => ({
    galaxy: null,
    unitsById: new Map(),
    controllablesById: new Map(),
    teamsById: new Map(),
    clustersById: new Map(),
    overlayBySessionId: new Map(),
    chatEntries: [],
    pendingCommands: new Map(),
    activityEntries: [],
  }),
  getters: {
    snapshot: (state) => state.galaxy,
    ownerOverlay: (state) => aggregateOverlayState(state.overlayBySessionId),
    worldStats: (state) => ({
      teams: state.galaxy?.teams.length ?? 0,
      clusters: state.galaxy?.clusters.length ?? 0,
      units: state.galaxy?.units.length ?? 0,
      controllables: state.galaxy?.controllables.length ?? 0,
    }),
    latestChatEntry: (state) => state.chatEntries[0] ?? null,
    ownedControllables: (state): OwnedControllableSummary[] => {
      const sessionStore = useSessionStore();
      const selectedSessionId = sessionStore.selectedPlayerSession?.playerSessionId ?? '';
      const overlayById = selectedSessionId ? state.overlayBySessionId.get(selectedSessionId) ?? new Map() : new Map();

      return Array.from(overlayById.entries()).map(([controllableId, overlay]) => {
        const publicControllable = state.galaxy?.controllables.find((item) => item.controllableId === controllableId);
        const overlayState = objectValue(overlay) ?? {};

        return {
          controllableId,
          displayName: publicControllable?.displayName ?? stringValue(overlayState.displayName, controllableId),
          teamName: publicControllable?.teamName ?? stringValue(overlayState.teamName, 'Unknown'),
          alive: getControllableAliveState(publicControllable, overlayState),
          score: publicControllable?.score ?? 0,
          kind: stringValue(overlayState.kind, 'unknown'),
        };
      });
    },
    activeControllable: (state) => (controllableId: string) => {
      if (!controllableId) {
        return null;
      }

      return state.galaxy?.controllables.find((entry) => entry.controllableId === controllableId) ?? null;
    },
    overlayEntry: (state) => (controllableId: string): OverlayEntry | null => {
      const overlayById = aggregateOverlayState(state.overlayBySessionId);
      if (!controllableId || !hasRecordKey(overlayById, controllableId)) {
        return null;
      }

      return buildOverlayEntry(controllableId, state.galaxy, new Map(Object.entries(overlayById) as Array<[string, ControllableOverlayState]>), controllableId);
    },
    selectionEntry: (state) => (selection: WorldSceneSelection | null): ClickedUnitEntry | null => {
      return buildClickedUnitEntry(selection, state.galaxy, new Map(Object.entries(aggregateOverlayState(state.overlayBySessionId)) as Array<[string, ControllableOverlayState]>));
    },
    scannerModeFor: (state) => (controllableId: string): ScannerMode => {
      if (!controllableId) {
        return 'off';
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerMode(resolveScannerState(overlayState.scanner));
    },
    scannerTargetFor: (state) => (controllableId: string): string | undefined => {
      if (!controllableId) {
        return undefined;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      const scannerState = resolveScannerState(overlayState.scanner);
      return optionalStringValue(scannerState?.targetUnitId);
    },
    scannerWidthFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 90;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerWidth(resolveScannerState(overlayState.scanner));
    },
    scannerWidthMinimumFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 5;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerMinimumWidth(resolveScannerState(overlayState.scanner));
    },
    scannerWidthMaximumFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 90;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerMaximumWidth(resolveScannerState(overlayState.scanner));
    },
    recentActivity: (state) => (lifetimeMs: number) => {
      const now = Date.now();
      return state.activityEntries.filter((entry) => now - entry.createdAt < lifetimeMs);
    },
  },
  actions: {
    applySnapshot(snapshot: GalaxySnapshotDto) {
      this.galaxy = cloneSnapshot(snapshot);
      rebuildIndexes(this);
    },
    applyWorldDelta(message: WorldDeltaMessage) {
      if (!this.galaxy) {
        return;
      }

      this.galaxy = applyWorldDeltaToSnapshot(this.galaxy, message);
      rebuildIndexes(this);
    },
    applyOwnerDelta(message: OwnerOverlayDeltaMessage) {
      const currentOverlay = this.overlayBySessionId.get(message.playerSessionId) ?? new Map();
      const next = applyOwnerOverlay(Object.fromEntries(currentOverlay.entries()), message);
      const nextOverlayBySessionId = new Map(this.overlayBySessionId);
      nextOverlayBySessionId.set(message.playerSessionId, new Map(Object.entries(next) as Array<[string, ControllableOverlayState]>));
      this.overlayBySessionId = nextOverlayBySessionId;
    },
    syncAttachedPlayerSessions(playerSessions: PlayerSessionSummaryDto[]) {
      this.overlayBySessionId = pruneOverlaySessions(this.overlayBySessionId, playerSessions);
    },
    addChatEntry(entry: ChatEntryDto) {
      this.chatEntries = [entry, ...this.chatEntries].slice(0, 12);
    },
    trackCommand(commandId: string, descriptor: PendingCommandDescriptor) {
      const next = new Map(this.pendingCommands);
      next.set(commandId, descriptor);
      this.pendingCommands = next;
    },
    resolveCommand(message: CommandReplyMessage) {
      const descriptor = consumePendingCommand(this, message.commandId);
      const summaryBase = descriptor?.subject ? `${descriptor.label} · ${descriptor.subject}` : descriptor?.label ?? humanizeCode(message.commandId);

      if (message.error) {
        this.recordActivity({
          id: `command-${message.commandId}`,
          tone: 'error',
          summary: `${summaryBase} failed`,
          detail: message.error.message,
          meta: message.error.code,
          createdAt: Date.now(),
        });
      } else if (message.status === 'completed') {
        this.recordActivity({
          id: `command-${message.commandId}`,
          tone: 'success',
          summary: `${summaryBase} completed`,
          detail: formatCommandReplyDetail(message, (controllableId) => this.getControllableLabel(controllableId)),
          createdAt: Date.now(),
        });
      } else {
        this.recordActivity({
          id: `command-${message.commandId}`,
          tone: message.status === 'accepted' ? 'info' : 'warn',
          summary: `${summaryBase} ${message.status}`,
          detail: formatCommandReplyDetail(message, (controllableId) => this.getControllableLabel(controllableId)),
          createdAt: Date.now(),
        });
      }

      if (message.status !== 'completed') {
        return undefined;
      }

      const action = optionalStringValue(message.result?.action);
      const controllableId = optionalStringValue(message.result?.controllableId);
      if (controllableId && action !== 'remove_requested') {
        return controllableId;
      }

      return undefined;
    },
    recordStatus(message: ServerStatusMessage) {
      this.recordActivity({
        id: `status-${Date.now()}-${Math.random().toString(16).slice(2)}`,
        tone: statusKindToTone(message.kind),
        summary: humanizeCode(message.code),
        detail: message.message,
        meta: buildStatusMeta(message),
        createdAt: Date.now(),
      });
    },
    recordActivity(entry: Omit<ActivityEntry, 'id' | 'createdAt'> & Partial<Pick<ActivityEntry, 'id' | 'createdAt'>>) {
      this.activityEntries = [
        {
          id: entry.id ?? `activity-${Date.now()}-${Math.random().toString(16).slice(2)}`,
          createdAt: entry.createdAt ?? Date.now(),
          tone: entry.tone,
          summary: entry.summary,
          detail: entry.detail,
          meta: entry.meta,
        },
        ...this.activityEntries,
      ].slice(0, activityHistoryLimit);
    },
    clearNavigationOverlay(controllableId: string) {
      const nextOverlayBySessionId = new Map(this.overlayBySessionId);
      let updated = false;

      for (const [playerSessionId, overlayById] of nextOverlayBySessionId.entries()) {
        const currentOverlay = objectValue(overlayById.get(controllableId));
        if (!currentOverlay || !hasRecordKey(currentOverlay, 'navigation')) {
          continue;
        }

        const nextOverlay = { ...currentOverlay };
        delete nextOverlay.navigation;

        const nextOverlayById = new Map(overlayById);
        nextOverlayById.set(controllableId, nextOverlay);
        nextOverlayBySessionId.set(playerSessionId, nextOverlayById);
        updated = true;
      }

      if (updated) {
        this.overlayBySessionId = nextOverlayBySessionId;
      }
    },
    clearOverlay() {
      this.overlayBySessionId = new Map();
    },
    clearAll() {
      this.galaxy = null;
      this.unitsById = new Map();
      this.controllablesById = new Map();
      this.teamsById = new Map();
      this.clustersById = new Map();
      this.overlayBySessionId = new Map();
      this.chatEntries = [];
      this.pendingCommands = new Map();
      this.activityEntries = [];
    },
    getControllableLabel(controllableId: string) {
      const owned = this.ownedControllables.find((entry) => entry.controllableId === controllableId);
      if (owned) {
        return owned.displayName;
      }

      return this.galaxy?.controllables.find((entry) => entry.controllableId === controllableId)?.displayName ?? controllableId;
    },
  },
});

function rebuildIndexes(store: GameState) {
  store.unitsById = new Map((store.galaxy?.units ?? []).map((unit) => [unit.unitId, unit]));
  store.controllablesById = new Map((store.galaxy?.controllables ?? []).map((controllable) => [controllable.controllableId, controllable]));
  store.teamsById = new Map((store.galaxy?.teams ?? []).map((team) => [team.id, team]));
  store.clustersById = new Map((store.galaxy?.clusters ?? []).map((cluster) => [cluster.id, cluster]));
}

function cloneSnapshot(source: GalaxySnapshotDto): GalaxySnapshotDto {
  return {
    ...source,
    teams: source.teams.map((team) => ({ ...team })),
    clusters: source.clusters.map((cluster) => ({ ...cluster })),
    units: source.units.map((unit) => ({
      ...unit,
      fullStateKnown: booleanValue(unit.fullStateKnown, false),
      isStatic: booleanValue(unit.isStatic, false),
      isSeen: booleanValue(unit.isSeen, true),
      lastSeenTick: numberValue(unit.lastSeenTick, 0),
    })),
    controllables: source.controllables.map((controllable) => ({ ...controllable })),
  };
}

function applyWorldDeltaToSnapshot(current: GalaxySnapshotDto, message: WorldDeltaMessage): GalaxySnapshotDto {
  const next = cloneSnapshot(current);

  for (const event of message.events) {
    if (event.eventType === 'unit.updated') {
      const unit = next.units.find((item) => item.unitId === event.entityId);
      if (!unit || !event.changes) {
        continue;
      }

      unit.clusterId = numberValue(event.changes.clusterId, unit.clusterId);
      unit.kind = stringValue(event.changes.kind, unit.kind);
      unit.fullStateKnown = booleanValue(event.changes.fullStateKnown, booleanValue(unit.fullStateKnown, false));
      unit.isStatic = booleanValue(event.changes.isStatic, unit.isStatic);
      unit.isSeen = booleanValue(event.changes.isSeen, unit.isSeen);
      unit.lastSeenTick = numberValue(event.changes.lastSeenTick, unit.lastSeenTick);
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
      applyOptionalUnitMetric(unit, 'planetMetal', event.changes.planetMetal);
      applyOptionalUnitMetric(unit, 'planetCarbon', event.changes.planetCarbon);
      applyOptionalUnitMetric(unit, 'planetHydrogen', event.changes.planetHydrogen);
      applyOptionalUnitMetric(unit, 'planetSilicon', event.changes.planetSilicon);
      continue;
    }

    if (event.eventType === 'unit.created' && event.changes) {
      next.units = [
        ...next.units,
        {
          unitId: event.entityId,
          clusterId: numberValue(event.changes.clusterId, 1),
          kind: stringValue(event.changes.kind, 'unknown'),
          fullStateKnown: booleanValue(event.changes.fullStateKnown, false),
          isStatic: booleanValue(event.changes.isStatic, false),
          isSeen: booleanValue(event.changes.isSeen, true),
          lastSeenTick: numberValue(event.changes.lastSeenTick, 0),
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
          planetMetal: optionalNumberValue(event.changes.planetMetal),
          planetCarbon: optionalNumberValue(event.changes.planetCarbon),
          planetHydrogen: optionalNumberValue(event.changes.planetHydrogen),
          planetSilicon: optionalNumberValue(event.changes.planetSilicon),
        },
      ];
      continue;
    }

    if (event.eventType === 'unit.removed') {
      next.units = next.units.filter((item) => item.unitId !== event.entityId);
      continue;
    }

    if (event.eventType === 'controllable.created' && event.changes) {
      const existing = next.controllables.find((item) => item.controllableId === event.entityId);
      if (existing) {
        existing.displayName = stringValue(event.changes.displayName, existing.displayName);
        existing.teamName = stringValue(event.changes.teamName, existing.teamName);
        existing.alive = booleanValue(event.changes.alive, existing.alive);
        existing.score = numberValue(event.changes.score, existing.score);
      } else {
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

function aggregateOverlayState(overlayBySessionId: Map<string, Map<string, ControllableOverlayState>>) {
  const aggregate: Record<string, unknown> = {};

  for (const overlayById of overlayBySessionId.values()) {
    for (const [controllableId, overlay] of overlayById.entries()) {
      aggregate[controllableId] = overlay;
    }
  }

  return aggregate;
}

function resolveOverlayByControllableId(
  overlayBySessionId: Map<string, Map<string, ControllableOverlayState>>,
  controllableId: string,
) {
  for (const overlayById of overlayBySessionId.values()) {
    const overlay = overlayById.get(controllableId);
    if (overlay) {
      return overlay;
    }
  }

  return undefined;
}

function pruneOverlaySessions(
  overlayBySessionId: Map<string, Map<string, ControllableOverlayState>>,
  playerSessions: PlayerSessionSummaryDto[],
) {
  const attachedSessionIds = new Set(playerSessions.map((player) => player.playerSessionId));
  return new Map(
    Array.from(overlayBySessionId.entries()).filter(([playerSessionId]) => attachedSessionIds.has(playerSessionId)),
  );
}

function consumePendingCommand(store: GameState, commandId: string) {
  const descriptor = store.pendingCommands.get(commandId);
  if (!descriptor) {
    return undefined;
  }

  const next = new Map(store.pendingCommands);
  next.delete(commandId);
  store.pendingCommands = next;
  return descriptor;
}

function applyOptionalUnitMetric(
  unit: UnitSnapshotDto,
  key: 'sunEnergy' | 'sunIons' | 'sunNeutrinos' | 'sunHeat' | 'sunDrain' | 'planetMetal' | 'planetCarbon' | 'planetHydrogen' | 'planetSilicon',
  value: unknown,
) {
  if (value === undefined) {
    return;
  }

  unit[key] = optionalNumberValue(value);
}

function getControllableAliveState(
  publicControllable: PublicControllableSnapshotDto | null | undefined,
  overlayState: Record<string, unknown>,
) {
  if (hasRecordKey(overlayState, 'alive')) {
    return booleanValue(overlayState.alive, publicControllable?.alive ?? true);
  }

  return publicControllable?.alive ?? true;
}

function buildOverlayEntry(
  controllableId: string,
  snapshot: GalaxySnapshotDto | null,
  overlayById: Map<string, ControllableOverlayState>,
  selectedControllableId: string,
): OverlayEntry | null {
  const overlayState = objectValue(overlayById.get(controllableId)) ?? {};
  const publicControllable = snapshot?.controllables.find((item) => item.controllableId === controllableId);
  const positionState = objectValue(overlayState.position);
  const movementState = objectValue(overlayState.movement);
  const engineState = objectValue(overlayState.engine);
  const scannerState = resolveScannerState(overlayState.scanner);
  const shieldState = objectValue(overlayState.shield);
  const alive = getControllableAliveState(publicControllable, overlayState);
  const scannerActive = booleanValue(scannerState?.active, false);
  const engineMax = numberValue(engineState?.maximum, 0);
  const engineLoad = engineMax > 0
    ? magnitude(numberValue(engineState?.currentX, 0), numberValue(engineState?.currentY, 0)) / engineMax
    : 0;
  const speed = magnitude(numberValue(movementState?.x, 0), numberValue(movementState?.y, 0));
  const clusterName = stringValue(overlayState.clusterName, 'Unknown cluster');
  const clusterId = numberValue(overlayState.clusterId, 0);

  const badges = [] as OverlayEntry['badges'];
  if (controllableId === selectedControllableId) {
    badges.push({ label: 'selected', tone: 'accent' });
  }

  const statusLabel = !alive
    ? 'Destroyed'
    : scannerActive
      ? `Scanner ${stringValue(scannerState?.mode, 'on')}`
      : 'Scanner offline';

  return {
    id: controllableId,
    displayName: publicControllable?.displayName ?? stringValue(overlayState.displayName, controllableId),
    kind: stringValue(overlayState.kind, 'unknown'),
    alive,
    clusterLabel: `${clusterName} · C${clusterId}`,
    badges,
    statusLabel,
    meters: [
      buildOverlayMeter('Hull', objectValue(overlayState.hull), 'hull', alive ? 'offline' : 'destroyed'),
      buildOverlayMeter('Shield', shieldState, 'shield', booleanValue(shieldState?.active, false) ? 'charging' : 'offline'),
      buildOverlayMeter('Battery', objectValue(overlayState.energyBattery), 'energy', 'offline'),
    ],
    stats: [
      { label: 'Ammo', value: formatWholeValue(overlayState.ammo) },
      { label: 'Speed', value: formatMetric(speed) },
      { label: 'Heading', value: formatAngle(numberValue(positionState?.angle, 0)) },
      { label: 'Drive', value: engineMax > 0 ? `${Math.round(clamp01(engineLoad) * 100)}%` : 'idle' },
    ],
  };
}

function buildClickedUnitEntry(
  selection: WorldSceneSelection | null,
  snapshot: GalaxySnapshotDto | null,
  overlayById: Map<string, ControllableOverlayState>,
): ClickedUnitEntry | null {
  const unitId = selection?.unitId;
  if (!selection || !unitId) {
    return null;
  }

  const publicUnit = snapshot?.units.find((item) => item.unitId === unitId);
  const publicControllable = snapshot?.controllables.find((item) => item.controllableId === unitId);
  const overlayState = objectValue(overlayById.get(unitId)) ?? {};
  const positionState = objectValue(overlayState.position);
  const movementState = objectValue(overlayState.movement);
  const engineState = objectValue(overlayState.engine);
  const scannerState = resolveScannerState(overlayState.scanner);
  const shieldState = objectValue(overlayState.shield);
  const hullState = objectValue(overlayState.hull);
  const batteryState = objectValue(overlayState.energyBattery);
  const clusterId = publicUnit?.clusterId ?? numberValue(overlayState.clusterId, 0);
  const clusterName = snapshot?.clusters.find((cluster) => cluster.id === clusterId)?.name
    ?? stringValue(overlayState.clusterName, clusterId > 0 ? `Cluster ${clusterId}` : 'Unknown cluster');
  const teamName = publicControllable?.teamName ?? publicUnit?.teamName ?? optionalStringValue(overlayState.teamName);
  const alive = getControllableAliveState(publicControllable, overlayState);
  const active = booleanValue(overlayState.active, false);
  const scannerActive = booleanValue(scannerState?.active, false);
  const fullStateKnown = booleanValue(publicUnit?.fullStateKnown, false);
  const speed = magnitude(numberValue(movementState?.x, 0), numberValue(movementState?.y, 0));
  const engineMax = numberValue(engineState?.maximum, 0);
  const engineLoad = engineMax > 0
    ? magnitude(numberValue(engineState?.currentX, 0), numberValue(engineState?.currentY, 0)) / engineMax
    : 0;
  const unitX = publicUnit?.x ?? numberValue(positionState?.x, selection.worldX);
  const unitY = publicUnit?.y ?? numberValue(positionState?.y, -selection.worldY);
  const angle = publicUnit?.angle ?? numberValue(positionState?.angle, 0);
  const kind = publicUnit?.kind ?? stringValue(overlayState.kind, selection.kind ?? 'unknown');
  const badges = [] as ClickedUnitEntry['badges'];

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
    badges.push({ label: scannerActive ? `scan ${stringValue(scannerState?.mode, '')}`.trim() : 'scan off', tone: scannerActive ? 'accent' : 'muted' });
  }

  if (publicUnit) {
    badges.push({ label: fullStateKnown ? 'full scan' : 'partial scan', tone: fullStateKnown ? 'ok' : 'muted' });
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
    meters.push(buildOverlayMeter('Shield', shieldState, 'shield', booleanValue(shieldState?.active, false) ? 'charging' : 'offline'));
  }
  if (batteryState || publicControllable) {
    meters.push(buildOverlayMeter('Battery', batteryState, 'energy', 'offline'));
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
    detailGroups: buildDetailGroups(publicUnit),
  };
}

function buildDetailGroups(unit: UnitSnapshotDto | undefined | null) {
  const groups = [] as Array<{ title: string; tone: 'solar' | 'hazard'; stats: OverlayStat[] }>;

  if (hasSunTelemetry(unit)) {
    groups.push(
      {
        title: 'Stellar Output',
        tone: 'solar' as const,
        stats: [
          { label: 'Photon Flux', value: formatMetric(unit?.sunEnergy ?? 0) },
          { label: 'Plasma Wind', value: formatMetric(unit?.sunIons ?? 0) },
          { label: 'Neutrino Flux', value: formatMetric(unit?.sunNeutrinos ?? 0) },
        ],
      },
      {
        title: 'Environmental Hazard',
        tone: 'hazard' as const,
        stats: [
          { label: 'Heat', value: `${formatMetric(unit?.sunHeat ?? 0)} · ${formatMetric((unit?.sunHeat ?? 0) * 15)} energy/tick` },
          { label: 'Radiation', value: `${formatMetric(unit?.sunDrain ?? 0)} · ${formatMetric((unit?.sunDrain ?? 0) * 0.125)} hull/tick` },
        ],
      },
    );
  }

  if (hasPlanetTelemetry(unit)) {
    groups.push({
      title: 'Planetary Composition',
      tone: 'solar' as const,
      stats: [
        { label: 'Metal', value: formatMetric(unit?.planetMetal ?? 0) },
        { label: 'Carbon', value: formatMetric(unit?.planetCarbon ?? 0) },
        { label: 'Hydrogen', value: formatMetric(unit?.planetHydrogen ?? 0) },
        { label: 'Silicon', value: formatMetric(unit?.planetSilicon ?? 0) },
      ],
    });
  }

  return groups;
}

function hasSunTelemetry(unit: UnitSnapshotDto | null | undefined) {
  return !!unit && [unit.sunEnergy, unit.sunIons, unit.sunNeutrinos, unit.sunHeat, unit.sunDrain].some((value) => typeof value === 'number');
}

function hasPlanetTelemetry(unit: UnitSnapshotDto | null | undefined) {
  return !!unit && [unit.planetMetal, unit.planetCarbon, unit.planetHydrogen, unit.planetSilicon].some((value) => typeof value === 'number');
}

function deriveScannerMode(scannerState: Record<string, unknown> | undefined): ScannerMode {
  if (!scannerState || !booleanValue(scannerState.active, false)) {
    return 'off';
  }

  const explicitMode = stringValue(scannerState.mode, '').toLowerCase();
  if (explicitMode === '360' || explicitMode === 'full') {
    return '360';
  }

  if (explicitMode === 'forward') {
    return 'forward';
  }

  if (explicitMode === 'target' || explicitMode === 'targeted') {
    return 'targeted';
  }

  if (explicitMode === 'sweep') {
    return 'sweep';
  }

  const targetWidth = numberValue(scannerState.targetWidth, numberValue(scannerState.currentWidth, 0));
  return targetWidth >= 180 ? '360' : 'forward';
}

function deriveScannerWidth(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 90;
  }

  return numberValue(
    scannerState.requestedWidth,
    numberValue(scannerState.targetWidth, numberValue(scannerState.currentWidth, deriveScannerMaximumWidth(scannerState))),
  );
}

function deriveScannerMinimumWidth(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 5;
  }

  return numberValue(scannerState.minimumWidth, 5);
}

function deriveScannerMaximumWidth(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 90;
  }

  return numberValue(scannerState.maximumWidth, 90);
}

function resolveScannerState(scannerValue: unknown) {
  const scannerState = objectValue(scannerValue);
  if (!scannerState) {
    return undefined;
  }

  if (looksLikeScannerState(scannerState)) {
    return scannerState;
  }

  for (const nestedValue of Object.values(scannerState)) {
    const nestedState = objectValue(nestedValue);
    if (nestedState && looksLikeScannerState(nestedState)) {
      return nestedState;
    }
  }

  return scannerState;
}

function looksLikeScannerState(value: Record<string, unknown>) {
  return hasRecordKey(value, 'active')
    || hasRecordKey(value, 'mode')
    || hasRecordKey(value, 'currentWidth')
    || hasRecordKey(value, 'targetWidth');
}

function buildOverlayMeter(label: string, state: Record<string, unknown> | undefined, tone: OverlayMeterTone, emptyLabel: string): OverlayMeter {
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
