import { defineStore } from 'pinia';
import {
  buildStatusMeta,
  clamp01,
  formatAngle,
  formatCommandReplyDetail,
  formatGravity,
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
import { isShortLivedProjectileKind } from '../lib/unitKinds';
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
  TrajectoryPointDto,
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
  TacticalMode,
} from '../types/client';
import { useSessionStore } from './session';
import { useUiStore } from './ui';

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
    teamScores: (state): TeamSnapshotDto[] => {
      return [...(state.galaxy?.teams ?? [])]
        .filter((team) => team.playable !== false)
        .sort((left, right) => {
        if (right.score !== left.score) {
          return right.score - left.score;
        }

        return left.name.localeCompare(right.name);
        });
    },
    ownedControllables: (state): OwnedControllableSummary[] => {
      const sessionStore = useSessionStore();
      const selectedSessionId = sessionStore.selectedPlayerSession?.playerSessionId ?? '';
      const overlayById = selectedSessionId ? state.overlayBySessionId.get(selectedSessionId) ?? new Map() : new Map();

      return Array.from(overlayById.entries())
        .filter(([, overlay]) => isOverlayCommandable(objectValue(overlay) ?? {}))
        .map(([controllableId, overlay]) => {
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

      const uiStore = useUiStore();
      return buildOverlayEntry(controllableId, state.galaxy, new Map(Object.entries(overlayById) as Array<[string, ControllableOverlayState]>), uiStore.selectedControllableId);
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
    scannerLengthFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 200;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerLength(resolveScannerState(overlayState.scanner));
    },
    scannerLengthMinimumFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 1;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerMinimumLength(resolveScannerState(overlayState.scanner));
    },
    scannerLengthMaximumFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 200;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      return deriveScannerMaximumLength(resolveScannerState(overlayState.scanner));
    },
    scannerRequestedModeFor: (state) => (controllableId: string): ScannerMode => {
      if (!controllableId) {
        return 'off';
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      const scannerState = resolveScannerState(overlayState.scanner);
      if (!scannerState) {
        return 'off';
      }

      const mode = stringValue(scannerState.mode, 'off').toLowerCase();
      if (mode === '360' || mode === 'full') return '360';
      if (mode === 'forward') return 'forward';
      if (mode === 'hold') return 'hold';
      if (mode === 'sweep') return 'sweep';
      if (mode === 'targeted' || mode === 'target') return 'targeted';
      return 'off';
    },
    tacticalModeFor: (state) => (controllableId: string): TacticalMode => {
      if (!controllableId) {
        return 'off';
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      const tacticalState = objectValue(overlayState.tactical);
      if (!tacticalState) {
        return 'off';
      }

      const mode = stringValue(tacticalState.mode, 'off').toLowerCase();
      if (mode === 'enemy') return 'enemy';
      if (mode === 'target') return 'target';
      return 'off';
    },
    maxSpeedFractionFor: (state) => (controllableId: string): number => {
      if (!controllableId) {
        return 1;
      }

      const overlayState = objectValue(resolveOverlayByControllableId(state.overlayBySessionId, controllableId)) ?? {};
      const navigationState = objectValue(overlayState.navigation);
      if (!navigationState) {
        return 1;
      }

      return numberValue(navigationState.maxSpeedFraction, 1);
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

      const previousGalaxy = this.galaxy;
      const nextGalaxy = applyWorldDeltaToSnapshot(previousGalaxy, message);
      this.galaxy = nextGalaxy;
      rebuildIndexes(this);
      this.recordTeamScoreActivities(previousGalaxy, nextGalaxy);
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
    recordTeamScoreActivities(previousGalaxy: GalaxySnapshotDto, nextGalaxy: GalaxySnapshotDto) {
      const previousScores = new Map(previousGalaxy.teams.map((team) => [team.id, team.score]));

      for (const team of nextGalaxy.teams) {
        const previousScore = previousScores.get(team.id);
        if (previousScore === undefined || team.score <= previousScore) {
          continue;
        }

        const pointsScored = team.score - previousScore;
        this.recordActivity({
          id: `team-score-${team.id}-${team.score}`,
          tone: 'success',
          summary: `${team.name} scored ${pointsScored === 1 ? 'a point' : `${pointsScored} points`}`,
          detail: `Score ${previousScore} -> ${team.score}`,
          meta: 'team score',
        });
      }
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
    teams: source.teams.map((team) => ({
      ...team,
      playable: booleanValue(team.playable, true),
    })),
    clusters: source.clusters.map((cluster) => ({ ...cluster })),
    units: pruneTransientHiddenUnits(dedupeUnitsById(source.units.map((unit) => ({
      ...unit,
      fullStateKnown: booleanValue(unit.fullStateKnown, false),
      isStatic: booleanValue(unit.isStatic, false),
      isSolid: booleanValue(unit.isSolid, true),
      isSeen: booleanValue(unit.isSeen, true),
      lastSeenTick: numberValue(unit.lastSeenTick, 0),
      gravity: numberValue(unit.gravity, 0),
      movementX: optionalNumberValue(unit.movementX),
      movementY: optionalNumberValue(unit.movementY),
      speedLimit: optionalNumberValue(unit.speedLimit),
      predictedTrajectory: normalizeTrajectoryPoints(unit.predictedTrajectory),
    })))),
    controllables: source.controllables.map((controllable) => ({ ...controllable })),
  };
}

function applyWorldDeltaToSnapshot(current: GalaxySnapshotDto, message: WorldDeltaMessage): GalaxySnapshotDto {
  const next = cloneSnapshot(current);

  for (const event of message.events) {
    if (event.eventType === 'team.removed') {
      const teamId = numberValue(event.changes?.id, Number(event.entityId));
      next.teams = next.teams.filter((team) => team.id !== teamId);
      continue;
    }

    if ((event.eventType === 'team.created' || event.eventType === 'team.updated') && event.changes) {
      const teamId = numberValue(event.changes.id, Number(event.entityId));
      const existing = next.teams.find((team) => team.id === teamId);
      if (existing) {
        existing.name = stringValue(event.changes.name, existing.name);
        existing.score = numberValue(event.changes.score, existing.score);
        existing.colorHex = stringValue(event.changes.colorHex, existing.colorHex);
        existing.playable = booleanValue(event.changes.playable, existing.playable);
      } else {
        next.teams = [
          ...next.teams,
          {
            id: teamId,
            name: stringValue(event.changes.name, `Team ${teamId}`),
            score: numberValue(event.changes.score, 0),
            colorHex: stringValue(event.changes.colorHex, '#808080'),
            playable: booleanValue(event.changes.playable, true),
          },
        ];
      }
      continue;
    }

    if (event.eventType === 'unit.updated') {
      if (!event.changes) {
        continue;
      }

      const unit = next.units.find((item) => item.unitId === event.entityId);
      if (unit) {
        applyUnitChanges(unit, event.changes);
      } else {
        next.units = [
          ...next.units,
          createUnitFromChanges(event.entityId, event.changes),
        ];
      }
      continue;
    }

    if (event.eventType === 'unit.created' && event.changes) {
      const existing = next.units.find((item) => item.unitId === event.entityId);
      if (existing) {
        applyUnitChanges(existing, event.changes);
      } else {
        next.units = [
          ...next.units,
          createUnitFromChanges(event.entityId, event.changes),
        ];
      }
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

  next.units = pruneTransientHiddenUnits(dedupeUnitsById(next.units));
  return next;
}

function createUnitFromChanges(unitId: string, changes: Record<string, unknown>): UnitSnapshotDto {
  const unit: UnitSnapshotDto = {
    unitId,
    clusterId: 1,
    kind: 'unknown',
    fullStateKnown: false,
    isStatic: false,
    isSolid: true,
    isSeen: true,
    lastSeenTick: 0,
    x: 0,
    y: 0,
    movementX: undefined,
    movementY: undefined,
    angle: 0,
    radius: 3,
    gravity: 0,
    speedLimit: undefined,
    predictedTrajectory: undefined,
    teamName: undefined,
    sunEnergy: undefined,
    sunIons: undefined,
    sunNeutrinos: undefined,
    sunHeat: undefined,
    sunDrain: undefined,
    planetMetal: undefined,
    planetCarbon: undefined,
    planetHydrogen: undefined,
    planetSilicon: undefined,
  };

  applyUnitChanges(unit, changes);
  return unit;
}

function applyUnitChanges(unit: UnitSnapshotDto, changes: Record<string, unknown>) {
  unit.clusterId = numberValue(changes.clusterId, unit.clusterId);
  unit.kind = stringValue(changes.kind, unit.kind);
  unit.fullStateKnown = booleanValue(changes.fullStateKnown, booleanValue(unit.fullStateKnown, false));
  unit.isStatic = booleanValue(changes.isStatic, unit.isStatic);
  unit.isSolid = booleanValue(changes.isSolid, booleanValue(unit.isSolid, true));
  unit.isSeen = booleanValue(changes.isSeen, unit.isSeen);
  unit.lastSeenTick = numberValue(changes.lastSeenTick, unit.lastSeenTick);
  unit.x = numberValue(changes.x, unit.x);
  unit.y = numberValue(changes.y, unit.y);
  if (hasRecordKey(changes, 'movementX')) {
    unit.movementX = optionalNumberValue(changes.movementX);
  }
  if (hasRecordKey(changes, 'movementY')) {
    unit.movementY = optionalNumberValue(changes.movementY);
  }
  unit.angle = numberValue(changes.angle, unit.angle);
  unit.radius = numberValue(changes.radius, unit.radius);
  unit.gravity = numberValue(changes.gravity, numberValue(unit.gravity, 0));
  if (hasRecordKey(changes, 'speedLimit')) {
    unit.speedLimit = optionalNumberValue(changes.speedLimit);
  }
  if (hasRecordKey(changes, 'predictedTrajectory')) {
    unit.predictedTrajectory = normalizeTrajectoryPoints(changes.predictedTrajectory);
  }
  unit.teamName = hasRecordKey(changes, 'teamName') ? optionalStringValue(changes.teamName) : unit.teamName;
  applyOptionalUnitMetric(unit, 'sunEnergy', changes.sunEnergy);
  applyOptionalUnitMetric(unit, 'sunIons', changes.sunIons);
  applyOptionalUnitMetric(unit, 'sunNeutrinos', changes.sunNeutrinos);
  applyOptionalUnitMetric(unit, 'sunHeat', changes.sunHeat);
  applyOptionalUnitMetric(unit, 'sunDrain', changes.sunDrain);
  applyOptionalUnitMetric(unit, 'planetMetal', changes.planetMetal);
  applyOptionalUnitMetric(unit, 'planetCarbon', changes.planetCarbon);
  applyOptionalUnitMetric(unit, 'planetHydrogen', changes.planetHydrogen);
  applyOptionalUnitMetric(unit, 'planetSilicon', changes.planetSilicon);
}

function dedupeUnitsById(units: UnitSnapshotDto[]) {
  const unitsById = new Map<string, UnitSnapshotDto>();
  for (const unit of units) {
    unitsById.set(unit.unitId, unit);
  }

  return Array.from(unitsById.values());
}

function pruneTransientHiddenUnits(units: UnitSnapshotDto[]) {
  return units.filter((unit) => !shouldDropTransientHiddenUnit(unit));
}

function shouldDropTransientHiddenUnit(unit: UnitSnapshotDto) {
  return !unit.isSeen && isShortLivedProjectileKind(unit.kind);
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

function normalizeTrajectoryPoints(value: unknown): TrajectoryPointDto[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  const points = value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => entry !== undefined)
    .map((entry) => ({
      x: numberValue(entry.x, 0),
      y: numberValue(entry.y, 0),
    }));

  return points.length > 0 ? points : undefined;
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

function isOverlayCommandable(overlayState: Record<string, unknown>) {
  if (hasRecordKey(overlayState, 'isCommandable')) {
    return booleanValue(overlayState.isCommandable, false);
  }

  return true;
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
  ];

  if (publicUnit) {
    stats.push({ label: 'Gravity', value: formatGravity(numberValue(publicUnit.gravity, 0)) });
  }

  stats.push({ label: 'Speed', value: formatMetric(speed) });

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
    detailGroups: buildDetailGroups(publicUnit, kind),
  };
}

function buildDetailGroups(unit: UnitSnapshotDto | undefined | null, kindHint?: string) {
  const groups = [] as Array<{ title: string; tone: 'solar' | 'hazard'; stats: OverlayStat[] }>;
  const normalizedKind = (kindHint ?? unit?.kind ?? '').toLowerCase();

  if (hasSunTelemetry(unit)) {
    groups.push(
      {
        title: 'Stellar Output',
        tone: 'solar' as const,
        stats: [
          { label: 'Photon Flux', value: formatOptionalMetric(unit?.sunEnergy) },
          { label: 'Plasma Wind', value: formatOptionalMetric(unit?.sunIons) },
          { label: 'Neutrino Flux', value: formatOptionalMetric(unit?.sunNeutrinos) },
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
        { label: 'Metal', value: formatPlanetMetric(unit?.planetMetal) },
        { label: 'Carbon', value: formatPlanetMetric(unit?.planetCarbon) },
        { label: 'Hydrogen', value: formatPlanetMetric(unit?.planetHydrogen) },
        { label: 'Silicon', value: formatPlanetMetric(unit?.planetSilicon) },
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

function formatOptionalMetric(value: number | null | undefined) {
  return typeof value === 'number' ? formatMetric(value) : 'Unknown';
}

function formatPlanetMetric(value: number | null | undefined) {
  return typeof value === 'number' ? formatMetric(value) : 'Unknown';
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

  if (explicitMode === 'hold') {
    return 'hold';
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

function deriveScannerLength(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 200;
  }

  return numberValue(
    scannerState.requestedLength,
    numberValue(scannerState.targetLength, numberValue(scannerState.currentLength, deriveScannerMaximumLength(scannerState))),
  );
}

function deriveScannerMinimumLength(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 1;
  }

  return numberValue(scannerState.minimumLength, 1);
}

function deriveScannerMaximumLength(scannerState: Record<string, unknown> | undefined) {
  if (!scannerState) {
    return 200;
  }

  return numberValue(scannerState.maximumLength, 200);
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
