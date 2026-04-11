<script setup lang="ts">
import { computed, ref } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { formatMetric } from '../../lib/formatting';
import { buildHarvestLabels } from '../../lib/harvesting';
import { isShipKind, normalizeKind } from '../../renderer/unitVisuals';
import { useGameStore } from '../../stores/game';
import { useSessionStore } from '../../stores/session';
import { useUiStore } from '../../stores/ui';
import type { PlayerSessionSummaryDto, PublicControllableSnapshotDto, UnitSnapshotDto } from '../../types/generated';

type OwnerOverlayRecord = Record<string, Record<string, unknown> | undefined>;

type RepresentedShip = {
  unitId: string;
  kind: string;
  unit: UnitSnapshotDto | undefined;
  controllable: PublicControllableSnapshotDto | undefined;
  overlay: Record<string, unknown> | undefined;
};

type SystemEntry = {
  unitId: string;
  displayName: string;
  kind: string;
  distance: number | null;
  isCurrent: boolean;
  isSeen: boolean;
  isVisible: boolean;
  harvestLabels?: string[];
};

const gateway = useGateway();
const gameStore = useGameStore();
const sessionStore = useSessionStore();
const uiStore = useUiStore();

const latestChatEntry = computed(() => gameStore.latestChatEntry);
const recentActivityEntries = computed(() => gameStore.recentActivity(8000).slice(0, 3));
const olderActivityCount = computed(() => Math.max(0, gameStore.activityEntries.length - recentActivityEntries.value.length));
const connectionIndicatorActionLabel = computed(() => sessionStore.showDisconnectAction ? 'Disconnect' : 'Connect');
const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const visibleUnitIds = computed(() => new Set(uiStore.visibleUnitIds));
const trackedUnitColors = computed(() => uiStore.trackedUnitColors);
const ownerOverlay = computed(() => gameStore.ownerOverlay as OwnerOverlayRecord);
const controllablesById = computed(() => new Map((gameStore.snapshot?.controllables ?? []).map((controllable) => [controllable.controllableId, controllable])));
const nonShipUnitCount = computed(() => {
  const snapshot = gameStore.snapshot;
  if (!snapshot) {
    return 0;
  }

  return snapshot.units.filter((unit) => !isShipUnit(unit.kind, controllablesById.value.has(unit.unitId))).length;
});
const representedShips = computed((): RepresentedShip[] => {
  const snapshot = gameStore.snapshot;
  if (!snapshot) {
    return [];
  }

  const shipsById = new Map<string, RepresentedShip>();

  for (const unit of snapshot.units) {
    if (!isShipUnit(unit.kind, controllablesById.value.has(unit.unitId))) {
      continue;
    }

    shipsById.set(unit.unitId, {
      unitId: unit.unitId,
      kind: unit.kind,
      unit,
      controllable: controllablesById.value.get(unit.unitId),
      overlay: ownerOverlay.value[unit.unitId],
    });
  }

  for (const controllable of snapshot.controllables) {
    if (shipsById.has(controllable.controllableId)) {
      continue;
    }

    const overlay = ownerOverlay.value[controllable.controllableId];
    shipsById.set(controllable.controllableId, {
      unitId: controllable.controllableId,
      kind: typeof overlay?.kind === 'string' && overlay.kind.length > 0 ? overlay.kind : 'ship',
      unit: undefined,
      controllable,
      overlay,
    });
  }

  return Array.from(shipsById.values());
});
const isUnitsPopoverOpen = ref(false);
const isShipsPopoverOpen = ref(false);
const isTeamsPopoverOpen = ref(false);
const isClustersPopoverOpen = ref(false);
const unitsSearchQuery = ref('');
const shipsSearchQuery = ref('');
const currentSystem = computed(() => {
  const snapshot = gameStore.snapshot;
  if (!snapshot) {
    return null;
  }

  const activeId = activeControllableId.value;
  const activeOverlay = activeId ? ownerOverlay.value[activeId] : undefined;
  const activeUnit = activeId ? snapshot.units.find((unit) => unit.unitId === activeId) : undefined;
  const activePosition = readUnitPosition(activeUnit, activeOverlay);
  const currentClusterId = readUnitClusterId(activeUnit, activeOverlay);
  if (currentClusterId === null) {
    return null;
  }

  return {
    clusterId: currentClusterId,
    clusterName: snapshot.clusters.find((cluster) => cluster.id === currentClusterId)?.name ?? `Cluster ${currentClusterId}`,
    activePosition,
    ownerOverlay: ownerOverlay.value,
    controllablesById: controllablesById.value,
    snapshot,
  };
});

const unitsInCurrentSystem = computed(() => {
  if (!isUnitsPopoverOpen.value || !currentSystem.value) {
    return [];
  }

  const {
    clusterId,
    clusterName,
    activePosition,
    ownerOverlay,
    controllablesById,
    snapshot,
  } = currentSystem.value;
  const activeId = activeControllableId.value;

  return snapshot.units
    .filter((unit) => unit.clusterId === clusterId)
    .filter((unit) => !isShipUnit(unit.kind, controllablesById.has(unit.unitId)))
    .map((unit) => {
      const controllable = controllablesById.get(unit.unitId);
      const position = readUnitPosition(unit, ownerOverlay[unit.unitId]);
      const isCurrent = unit.unitId === activeId;
      const isSeen = isCurrent || unit.isSeen;

      return {
        unitId: unit.unitId,
        displayName: controllable?.displayName ?? unit.unitId,
        kind: unit.kind,
        x: position.x,
        y: position.y,
        distance: activePosition ? Math.hypot(position.x - activePosition.x, position.y - activePosition.y) : null,
        isCurrent,
        isSeen,
        isVisible: visibleUnitIds.value.has(unit.unitId),
        clusterName,
        harvestLabels: buildHarvestLabels(unit),
      };
    })
    .sort((left, right) => compareTrackedSystemEntries(left, right));
});
const shipEntries = computed(() => {
  if (!isShipsPopoverOpen.value || !currentSystem.value) {
    return [];
  }

  const snapshot = currentSystem.value.snapshot;
  const clusterId = currentSystem.value.clusterId;
  const clusterName = currentSystem.value.clusterName;
  const activePosition = currentSystem.value?.activePosition ?? null;
  const activeId = activeControllableId.value;

  return representedShips.value
    .filter((ship) => readUnitClusterId(ship.unit, ship.overlay) === clusterId)
    .map((ship) => {
      const position = readKnownUnitPosition(ship.unit, ship.overlay);
      const isCurrent = ship.unitId === activeId;
      const isSeen = isCurrent || ship.unit?.isSeen || !!ship.overlay;

      return {
        unitId: ship.unitId,
        displayName: ship.controllable?.displayName ?? ship.unitId,
        kind: ship.kind,
        x: position?.x ?? null,
        y: position?.y ?? null,
        distance: activePosition && position ? Math.hypot(position.x - activePosition.x, position.y - activePosition.y) : null,
        isCurrent,
        isSeen,
        isVisible: visibleUnitIds.value.has(ship.unitId),
        clusterName,
      };
    })
    .sort((left, right) => {
      return compareTrackedSystemEntries(left, right);
    });
});
const filteredUnitsInCurrentSystem = computed(() => filterSystemEntries(unitsInCurrentSystem.value, unitsSearchQuery.value));
const filteredShipEntries = computed(() => filterSystemEntries(shipEntries.value, shipsSearchQuery.value));
const representedShipCount = computed(() => {
  if (!currentSystem.value) {
    return 0;
  }

  return representedShips.value.filter((ship) => readUnitClusterId(ship.unit, ship.overlay) === currentSystem.value?.clusterId).length;
});
const teamSummaries = computed(() => {
  if (!isTeamsPopoverOpen.value) {
    return [];
  }

  const snapshot = gameStore.snapshot;
  if (!snapshot) {
    return [];
  }

  return snapshot.teams
    .map((team) => {
      const shipCount = representedShips.value.filter((ship) => ship.controllable?.teamName === team.name || ship.unit?.teamName === team.name).length;
      const unitCount = snapshot.units.filter((unit) => !isShipUnit(unit.kind, controllablesById.value.has(unit.unitId)) && unit.teamName === team.name).length;

      return {
        id: team.id,
        name: team.name,
        score: team.score,
        colorHex: team.colorHex,
        shipCount,
        unitCount,
      };
    })
    .sort((left, right) => {
      if (left.score !== right.score) {
        return right.score - left.score;
      }

      return left.name.localeCompare(right.name);
    });
});
const clusterSummaries = computed(() => {
  if (!isClustersPopoverOpen.value) {
    return [];
  }

  const snapshot = gameStore.snapshot;
  if (!snapshot) {
    return [];
  }

  return snapshot.clusters
    .map((cluster) => {
      const shipCount = representedShips.value.filter((ship) => readUnitClusterId(ship.unit, ship.overlay) === cluster.id).length;
      const unitCount = snapshot.units.filter((unit) => unit.clusterId === cluster.id && !isShipUnit(unit.kind, controllablesById.value.has(unit.unitId))).length;

      return {
        id: cluster.id,
        name: cluster.name,
        isStart: cluster.isStart,
        respawns: cluster.respawns,
        shipCount,
        unitCount,
      };
    })
    .sort((left, right) => left.name.localeCompare(right.name));
});

const currentSystemLabel = computed(() => currentSystem.value?.clusterName ?? 'Unknown system');
const canQuickSwitchSessions = computed(() => sessionStore.attachedPlayerSessions.length > 0);
const selectedPlayerSessionId = computed(() => sessionStore.selectedPlayerSession?.playerSessionId ?? '');
const selectedPlayerLabel = computed(() => `${sessionStore.selectedPlayerSession?.displayName ?? 'Observer'} (${sessionStore.attachedPlayerSessions.length})`);

function onConnectionIndicatorClick(): void {
  if (sessionStore.showDisconnectAction) {
    gateway.disconnect();
    return;
  }

  gateway.connect();
}

function openUnitsPopover(): void {
  isUnitsPopoverOpen.value = true;
  isShipsPopoverOpen.value = false;
  isTeamsPopoverOpen.value = false;
  isClustersPopoverOpen.value = false;
}

function closeUnitsPopover(): void {
  isUnitsPopoverOpen.value = false;
  unitsSearchQuery.value = '';
}

function openShipsPopover(): void {
  isShipsPopoverOpen.value = true;
  isUnitsPopoverOpen.value = false;
  isTeamsPopoverOpen.value = false;
  isClustersPopoverOpen.value = false;
}

function closeShipsPopover(): void {
  isShipsPopoverOpen.value = false;
  shipsSearchQuery.value = '';
}

function openTeamsPopover(): void {
  isTeamsPopoverOpen.value = true;
  isUnitsPopoverOpen.value = false;
  isShipsPopoverOpen.value = false;
  isClustersPopoverOpen.value = false;
}

function closeTeamsPopover(): void {
  isTeamsPopoverOpen.value = false;
}

function openClustersPopover(): void {
  isClustersPopoverOpen.value = true;
  isUnitsPopoverOpen.value = false;
  isShipsPopoverOpen.value = false;
  isTeamsPopoverOpen.value = false;
}

function closeClustersPopover(): void {
  isClustersPopoverOpen.value = false;
}

function onTrackUnit(unitId: string): void {
  uiStore.toggleTrackedUnit(unitId);
}

function onNavigateUnit(worldX: number | null, worldY: number | null): void {
  const controllableId = activeControllableId.value;
  if (!controllableId || worldX === null || worldY === null) {
    return;
  }

  gateway.setNavigationTarget(
    controllableId,
    worldX,
    worldY,
    uiStore.navigationMaxSpeedFraction,
  );
}

function readNumeric(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

function readUnitPosition(
  unit: { x: number; y: number } | undefined,
  overlay: Record<string, unknown> | undefined,
) {
  const overlayPosition = overlay?.position;
  const positionRecord = typeof overlayPosition === 'object' && overlayPosition !== null
    ? overlayPosition as Record<string, unknown>
    : undefined;

  return {
    x: typeof positionRecord?.x === 'number' && Number.isFinite(positionRecord.x) ? positionRecord.x : unit?.x ?? 0,
    y: typeof positionRecord?.y === 'number' && Number.isFinite(positionRecord.y) ? positionRecord.y : unit?.y ?? 0,
  };
}

function readKnownUnitPosition(
  unit: { x: number; y: number } | undefined,
  overlay: Record<string, unknown> | undefined,
) {
  const overlayPosition = overlay?.position;
  const positionRecord = typeof overlayPosition === 'object' && overlayPosition !== null
    ? overlayPosition as Record<string, unknown>
    : undefined;
  const x = typeof positionRecord?.x === 'number' && Number.isFinite(positionRecord.x) ? positionRecord.x : unit?.x;
  const y = typeof positionRecord?.y === 'number' && Number.isFinite(positionRecord.y) ? positionRecord.y : unit?.y;

  if (typeof x !== 'number' || !Number.isFinite(x) || typeof y !== 'number' || !Number.isFinite(y)) {
    return null;
  }

  return { x, y };
}

function isShipUnit(kind: string, hasControllable: boolean) {
  return hasControllable || isShipKind(normalizeKind(kind));
}

function readUnitClusterId(
  unit: { clusterId: number } | undefined,
  overlay: Record<string, unknown> | undefined,
) {
  return unit?.clusterId ?? readNumeric(overlay?.clusterId);
}

function humanizeUnitKind(kind: string) {
  const normalized = kind
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();

  if (!normalized) {
    return 'Unknown unit';
  }

  return normalized.charAt(0).toUpperCase() + normalized.slice(1);
}

function filterSystemEntries<
  T extends Pick<SystemEntry, 'unitId' | 'displayName' | 'kind'>,
>(entries: T[], query: string) {
  const normalizedQuery = query.trim().toLowerCase();
  if (!normalizedQuery) {
    return entries;
  }

  return entries.filter((entry) => {
    const searchable = `${entry.displayName}\n${entry.unitId}\n${humanizeUnitKind(entry.kind)}`.toLowerCase();
    return searchable.includes(normalizedQuery);
  });
}

function isUnitTracked(unitId: string) {
  return !!trackedUnitColors.value[unitId];
}

function compareTrackedSystemEntries(left: SystemEntry, right: SystemEntry) {
  const leftTracked = isUnitTracked(left.unitId);
  const rightTracked = isUnitTracked(right.unitId);
  if (leftTracked !== rightTracked) {
    return leftTracked ? -1 : 1;
  }

  return left.displayName.localeCompare(right.displayName);
}

function trackedUnitButtonStyle(unitId: string) {
  const color = trackedUnitColors.value[unitId];
  return color ? { '--track-color': color } : undefined;
}

function canNavigateToUnit(worldX: number | null, worldY: number | null) {
  return worldX !== null && worldY !== null;
}

function formatOptionalMetric(value: number | null) {
  return value === null ? 'Unknown' : formatMetric(value);
}

function formatOptionalDistance(value: number | null, isCurrent: boolean) {
  if (isCurrent) {
    return 'Current';
  }

  return value === null ? 'Unknown' : `${formatMetric(value)} away`;
}

function onPlayerSessionChange(event: Event): void {
  const target = event.target as HTMLSelectElement | null;
  const playerSessionId = target?.value ?? '';
  if (!playerSessionId || playerSessionId === selectedPlayerSessionId.value) {
    return;
  }

  gateway.selectPlayerSession(playerSessionId);
}

function formatPlayerSessionOptionLabel(player: PlayerSessionSummaryDto): string {
  return player.teamName ? `${player.displayName} · ${player.teamName}` : player.displayName;
}
</script>

<template>
  <footer class="overlay-status-bar">
    <div class="status-bar-group status-bar-group-left">
      <button
        :class="[
          'status-bar-item',
          'status-bar-item-primary',
          'status-bar-connection-indicator',
          `is-${sessionStore.connectionState}`,
          'is-clickable'
        ]"
        type="button"
        :title="connectionIndicatorActionLabel"
        @click="onConnectionIndicatorClick"
      >
        <span class="status-bar-icon" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <circle cx="8" cy="8" r="3.25" fill="currentColor"></circle>
            <path d="M8 1.75v2.1M8 12.15v2.1M1.75 8h2.1M12.15 8h2.1" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4"></path>
          </svg>
        </span>
        <span class="status-bar-text">{{ sessionStore.connectionStateLabel }}</span>
      </button>

      <div :class="['status-bar-item', 'status-bar-session-trigger', { 'is-clickable': canQuickSwitchSessions }]">
        <span class="status-bar-icon" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <circle cx="8" cy="5.1" r="2.25" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
            <path d="M3.1 13.15c.75-2.2 2.55-3.3 4.9-3.3s4.15 1.1 4.9 3.3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4"></path>
          </svg>
        </span>
        <span class="status-bar-text">{{ selectedPlayerLabel }}</span>
        <span v-if="canQuickSwitchSessions" class="status-bar-session-caret" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <path d="M4.25 6.1 8 9.9l3.75-3.8" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.4"></path>
          </svg>
        </span>
        <select
          v-if="canQuickSwitchSessions"
          class="status-bar-session-native-select"
          :value="selectedPlayerSessionId"
          title="Switch active player"
          aria-label="Switch active player"
          @change="onPlayerSessionChange"
        >
          <option value="" :disabled="!!sessionStore.selectedPlayerSession">Observer</option>
          <option
            v-for="player in sessionStore.attachedPlayerSessions"
            :key="player.playerSessionId"
            :value="player.playerSessionId"
          >
            {{ formatPlayerSessionOptionLabel(player) }}
          </option>
        </select>
      </div>

      <div class="status-bar-item status-bar-actions">
        <button
          class="button-secondary button-compact status-bar-button status-bar-icon-button"
          type="button"
          title="Players"
          aria-label="Manage players"
          @click="uiStore.isManagerPopupOpen = true"
        >
          <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
            <path d="M9.75 2.1a2.15 2.15 0 1 0-2.45 3.36l-4 4a1.45 1.45 0 0 0 2.05 2.05l4-4a2.15 2.15 0 0 0 3.36-2.45L9.75 6.6l-.95-.95 1.95-1.95Z" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2"></path>
          </svg>
          <span class="sr-only">Players</span>
        </button>
      </div>
    </div>

    <div class="status-bar-group status-bar-group-center">
      <div
        class="status-bar-item status-bar-item-metric status-bar-item-metric-popover"
        title="Teams in snapshot"
        tabindex="0"
        @mouseenter="openTeamsPopover"
        @mouseleave="closeTeamsPopover"
        @focusin="openTeamsPopover"
        @focusout="closeTeamsPopover"
      >
        <span class="status-bar-icon" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <circle cx="5" cy="5.25" r="1.55" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
            <circle cx="11" cy="5.25" r="1.55" fill="none" stroke="currentColor" stroke-width="1.4"></circle>
            <path d="M2.7 12.8c.45-1.9 1.7-2.9 3.35-2.9 1.2 0 2.2.45 2.95 1.35M7.4 12.8c.45-1.9 1.7-2.9 3.35-2.9 1.7 0 2.95 1 3.4 2.9" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
          </svg>
        </span>
        <span class="status-bar-label">Teams</span>
        <strong>{{ gameStore.worldStats.teams }}</strong>

        <section v-if="isTeamsPopoverOpen" class="status-bar-popover panel-glass" aria-label="Teams in snapshot">
          <header class="status-bar-popover-head">
            <div>
              <h3>Teams</h3>
              <p>{{ teamSummaries.length }} teams in the snapshot</p>
            </div>
          </header>

          <div v-if="teamSummaries.length > 0" class="status-bar-popover-body">
            <ul class="status-bar-system-list">
              <li
                v-for="team in teamSummaries"
                :key="team.id"
                class="status-bar-system-row"
              >
                <div class="status-bar-system-info">
                  <div class="status-bar-system-copy status-bar-system-copy--stack">
                    <strong>
                      <span class="status-bar-team-swatch" :style="{ '--team-color': team.colorHex }"></span>
                      {{ team.name }}
                    </strong>
                    <span class="status-bar-system-copy-label">Score {{ formatMetric(team.score) }}</span>
                  </div>
                  <div class="status-bar-system-metrics">
                    <span>{{ team.shipCount }} ships</span>
                    <span>{{ team.unitCount }} units</span>
                    <span>{{ team.colorHex }}</span>
                  </div>
                </div>
              </li>
            </ul>
          </div>

          <p v-else class="status-bar-popover-empty">No teams are available in the current snapshot.</p>
        </section>
      </div>

      <div
        class="status-bar-item status-bar-item-metric status-bar-item-metric-popover"
        title="Clusters in snapshot"
        tabindex="0"
        @mouseenter="openClustersPopover"
        @mouseleave="closeClustersPopover"
        @focusin="openClustersPopover"
        @focusout="closeClustersPopover"
      >
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
        <strong>{{ gameStore.worldStats.clusters }}</strong>

        <section v-if="isClustersPopoverOpen" class="status-bar-popover panel-glass" aria-label="Clusters in snapshot">
          <header class="status-bar-popover-head">
            <div>
              <h3>Clusters</h3>
              <p>{{ clusterSummaries.length }} clusters in the snapshot</p>
            </div>
          </header>

          <div v-if="clusterSummaries.length > 0" class="status-bar-popover-body">
            <ul class="status-bar-system-list">
              <li
                v-for="cluster in clusterSummaries"
                :key="cluster.id"
                class="status-bar-system-row"
              >
                <div class="status-bar-system-info">
                  <div class="status-bar-system-copy status-bar-system-copy--stack">
                    <strong>{{ cluster.name }}</strong>
                    <span class="status-bar-system-copy-label">Cluster {{ cluster.id }}</span>
                  </div>
                  <div class="status-bar-system-metrics">
                    <span>{{ cluster.shipCount }} ships</span>
                    <span>{{ cluster.unitCount }} units</span>
                    <span v-if="cluster.isStart">Start</span>
                    <span v-if="cluster.respawns">Respawns</span>
                  </div>
                </div>
              </li>
            </ul>
          </div>

          <p v-else class="status-bar-popover-empty">No clusters are available in the current snapshot.</p>
        </section>
      </div>

      <div
        class="status-bar-item status-bar-item-metric status-bar-item-metric-popover"
        title="Non-ship units in snapshot"
        tabindex="0"
        @mouseenter="openUnitsPopover"
        @mouseleave="closeUnitsPopover"
        @focusin="openUnitsPopover"
        @focusout="closeUnitsPopover"
      >
        <span class="status-bar-icon" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <path d="M8 2.2 13.3 5v6L8 13.8 2.7 11V5z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.3"></path>
            <path d="M8 2.2V13.8M2.7 5 8 8l5.3-3" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.1"></path>
          </svg>
        </span>
        <span class="status-bar-label">Units</span>
        <strong>{{ nonShipUnitCount }}</strong>

        <section v-if="isUnitsPopoverOpen" class="status-bar-popover panel-glass" aria-label="Units by system">
          <header class="status-bar-popover-head">
            <div>
              <h3>{{ currentSystemLabel }}</h3>
              <p>{{ filteredUnitsInCurrentSystem.length }} / {{ unitsInCurrentSystem.length }} non-ship units in the current system</p>
            </div>
          </header>

          <div v-if="filteredUnitsInCurrentSystem.length > 0" class="status-bar-popover-body">
            <ul class="status-bar-system-list">
              <li
                v-for="unit in filteredUnitsInCurrentSystem"
                :key="unit.unitId"
                class="status-bar-system-row"
              >
                <div
                  class="status-bar-system-info"
                  :class="{
                    'is-visible': unit.isVisible,
                    'is-seen': unit.isSeen,
                  }"
                >
                  <div class="status-bar-system-copy">
                    <strong>{{ unit.displayName }}</strong>
                  </div>
                  <div class="status-bar-system-metrics">
                    <span>{{ humanizeUnitKind(unit.kind) }}</span>
                    <span>{{ unit.distance === null ? 'Current' : `${formatMetric(unit.distance)} away` }}</span>
                    <span>x {{ formatMetric(unit.x) }}</span>
                    <span>y {{ formatMetric(unit.y) }}</span>
                    <span
                      v-for="label in unit.harvestLabels ?? []"
                      :key="`${unit.unitId}-${label}`"
                      class="status-bar-resource-chip"
                    >
                      {{ label }}
                    </span>
                  </div>
                </div>
                <div class="status-bar-system-actions">
                  <button
                    type="button"
                    class="status-bar-unit-action"
                    :class="{ 'is-active': isUnitTracked(unit.unitId) }"
                    :style="trackedUnitButtonStyle(unit.unitId)"
                    @click.stop="onTrackUnit(unit.unitId)"
                  >
                    {{ isUnitTracked(unit.unitId) ? 'Tracked' : 'Track' }}
                  </button>
                  <button
                    type="button"
                    class="status-bar-unit-action"
                    :disabled="!canNavigateToUnit(unit.x, unit.y)"
                    @click.stop="onNavigateUnit(unit.x, unit.y)"
                  >
                    Navigate
                  </button>
                </div>
              </li>
            </ul>
          </div>

          <p v-else class="status-bar-popover-empty">
            {{
              currentSystem
                ? (unitsSearchQuery.trim() ? 'No non-ship units match the current search.' : 'No non-ship units are currently listed in this system.')
                : 'No current system is available for the active ship.'
            }}
          </p>

          <div class="status-bar-popover-search">
            <input
              v-model.trim="unitsSearchQuery"
              type="search"
              placeholder="Search units"
              aria-label="Search units"
            />
          </div>
        </section>
      </div>

      <div
        class="status-bar-item status-bar-item-metric status-bar-item-metric-popover"
        :title="currentSystem ? 'Ships in the current system' : 'Ships for the active ship system'"
        tabindex="0"
        @mouseenter="openShipsPopover"
        @mouseleave="closeShipsPopover"
        @focusin="openShipsPopover"
        @focusout="closeShipsPopover"
      >
        <span class="status-bar-icon" aria-hidden="true">
          <svg viewBox="0 0 16 16" focusable="false">
            <path d="M8 2.25 11.1 5.8 8 13.75 4.9 5.8z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.3"></path>
            <path d="M6.2 6.15h3.6" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
          </svg>
        </span>
        <span class="status-bar-label">Ships</span>
        <strong>{{ representedShipCount }}</strong>

        <section v-if="isShipsPopoverOpen" class="status-bar-popover panel-glass" aria-label="Ships by system">
          <header class="status-bar-popover-head">
            <div>
              <h3>{{ currentSystemLabel }}</h3>
              <p>{{ filteredShipEntries.length }} / {{ shipEntries.length }} ships in the current system</p>
            </div>
          </header>

          <div v-if="filteredShipEntries.length > 0" class="status-bar-popover-body">
            <ul class="status-bar-system-list">
              <li
                v-for="unit in filteredShipEntries"
                :key="unit.unitId"
                class="status-bar-system-row"
              >
                <div
                  class="status-bar-system-info"
                  :class="{
                    'is-visible': unit.isVisible,
                    'is-seen': unit.isSeen,
                  }"
                >
                  <div class="status-bar-system-copy">
                    <strong>{{ unit.displayName }}</strong>
                  </div>
                  <div class="status-bar-system-metrics">
                    <span>{{ humanizeUnitKind(unit.kind) }}</span>
                    <span>{{ unit.clusterName }}</span>
                    <span>{{ formatOptionalDistance(unit.distance, unit.isCurrent) }}</span>
                    <span>x {{ formatOptionalMetric(unit.x) }}</span>
                    <span>y {{ formatOptionalMetric(unit.y) }}</span>
                  </div>
                </div>
                <div class="status-bar-system-actions">
                  <button
                    type="button"
                    class="status-bar-unit-action"
                    :class="{ 'is-active': isUnitTracked(unit.unitId) }"
                    :style="trackedUnitButtonStyle(unit.unitId)"
                    @click.stop="onTrackUnit(unit.unitId)"
                  >
                    {{ isUnitTracked(unit.unitId) ? 'Tracked' : 'Track' }}
                  </button>
                  <button type="button" class="status-bar-unit-action" @click.stop="onNavigateUnit(unit.x, unit.y)">Navigate</button>
                </div>
              </li>
            </ul>
          </div>

          <p v-else class="status-bar-popover-empty">
            {{
              currentSystem
                ? (shipsSearchQuery.trim() ? 'No ships match the current search.' : 'No ships are currently listed in this system.')
                : 'No current system is available for the active ship.'
            }}
          </p>

          <div class="status-bar-popover-search">
            <input
              v-model.trim="shipsSearchQuery"
              type="search"
              placeholder="Search ships"
              aria-label="Search ships"
            />
          </div>
        </section>
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
        <button
          class="button-secondary button-compact status-bar-button status-bar-icon-button"
          type="button"
          title="Open chat"
          @click="uiStore.isChatPopupOpen = true"
        >
          <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
            <path d="M2.5 13.2 13.75 8 2.5 2.8l1.65 4.15L9 8l-4.85 1.05z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.2"></path>
          </svg>
        </button>
      </div>

      <div class="status-bar-item status-bar-item-debug">
        <button
          :class="['button-secondary', 'button-compact', 'status-bar-button', 'status-bar-icon-button', { 'is-active': uiStore.isDebugLogOpen }]"
          type="button"
          :title="`Debug log (${uiStore.debugLogEntries.length})`"
          @click="uiStore.setDebugLogOpen(!uiStore.isDebugLogOpen)"
        >
          <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
            <path d="M5 2.75h6M6.15 1.75h3.7M5.1 8h5.8M3.25 5.15h9.5v6.1a1.5 1.5 0 0 1-1.5 1.5h-6.5a1.5 1.5 0 0 1-1.5-1.5zM2.2 8h1.05M12.75 8h1.05M5.2 12.75v1.05M10.8 12.75v1.05" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.15"></path>
          </svg>
          <span v-if="uiStore.debugLogEntries.length > 0" class="status-bar-icon-badge">{{ uiStore.debugLogEntries.length }}</span>
        </button>
      </div>

      <div class="status-bar-item status-bar-item-history">
        <button
          class="button-secondary button-compact status-bar-button status-bar-icon-button"
          type="button"
          :disabled="gameStore.activityEntries.length === 0"
          :title="olderActivityCount > 0 ? `Older messages (${olderActivityCount})` : 'Message history'"
          @click="uiStore.isActivityHistoryOpen = true"
        >
          <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
            <path d="M8 2.25a3.25 3.25 0 0 0-3.25 3.25v1.35c0 .56-.2 1.1-.56 1.53L3 9.75h10l-1.19-1.37a2.3 2.3 0 0 1-.56-1.53V5.5A3.25 3.25 0 0 0 8 2.25Z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.2"></path>
            <path d="M6.4 11.35a1.73 1.73 0 0 0 3.2 0" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2"></path>
          </svg>
          <span v-if="olderActivityCount > 0" class="status-bar-icon-badge">{{ olderActivityCount }}</span>
        </button>
      </div>
    </div>
  </footer>
</template>



