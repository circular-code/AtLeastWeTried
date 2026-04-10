<script setup lang="ts">
import { computed } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { objectValue } from '../../lib/validation';
import GaugeMeter from '../../shared/GaugeMeter.vue';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';

type ShipSubsystemStat = {
  label: string;
  value: string;
};

type ShipSubsystemEntry = {
  id: string;
  name: string;
  status: string;
  stats: ShipSubsystemStat[];
};

type ResourceMinerState = {
  status: string;
  rate: string;
  yield: string;
};

const gateway = useGateway();
const gameStore = useGameStore();
const uiStore = useUiStore();

const selectionEntry = computed(() => gameStore.selectionEntry(uiStore.lastSelection));
const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const scannerMode = computed(() => gameStore.scannerModeFor(activeControllableId.value));
const scannerTargetId = computed(() => gameStore.scannerTargetFor(activeControllableId.value) ?? '');
const trackedUnitColors = computed(() => uiStore.trackedUnitColors);
const tacticalMode = computed(() => uiStore.tacticalMode);
const ownerOverlay = computed(() => gameStore.ownerOverlay as Record<string, Record<string, unknown> | undefined>);
const activeOverlayState = computed(() => ownerOverlay.value[activeControllableId.value] ?? {});
const activeSubsystems = computed(() => readSubsystemEntries(activeOverlayState.value.subsystems ?? activeOverlayState.value.modules));
const resourceMinerState = computed(() => readResourceMinerState(activeSubsystems.value));
const canInitiateTargetedScan = computed(() => {
  if (!selectionEntry.value || !activeControllableId.value) {
    return false;
  }

  return selectionEntry.value.id !== activeControllableId.value;
});
const canSetAsTarget = computed(() => {
  if (!selectionEntry.value || !activeControllableId.value) {
    return false;
  }

  return tacticalMode.value === 'target' && selectionEntry.value.id !== activeControllableId.value;
});
const canCollectResources = computed(() => {
  if (!selectionEntry.value || !activeControllableId.value) {
    return false;
  }

  return selectionEntry.value.id !== activeControllableId.value
    && isResourceCollectionTargetKind(selectionEntry.value.kind);
});
const selectionRuntimeStatus = computed(() => {
  const segments: string[] = [];

  if (selectionEntry.value && scannerMode.value === 'targeted' && scannerTargetId.value === selectionEntry.value.id) {
    segments.push('Target scan active');
  }

  if (resourceMinerState.value) {
    const minerSegments = [`Miner ${resourceMinerState.value.status}`];
    if (resourceMinerState.value.rate) {
      minerSegments.push(`Rate ${resourceMinerState.value.rate}`);
    }
    if (resourceMinerState.value.yield) {
      minerSegments.push(`Yield ${resourceMinerState.value.yield}`);
    }
    segments.push(minerSegments.join(' - '));
  }

  return segments.join(' | ');
});

function initiateTargetedScan() {
  if (!selectionEntry.value || !activeControllableId.value) {
    return;
  }

  gateway.initiateTargetedScan(activeControllableId.value, selectionEntry.value.id);
}

function toggleTrackedSelection() {
  if (!selectionEntry.value) {
    return;
  }

  uiStore.toggleTrackedUnit(selectionEntry.value.id);
}

function isUnitTracked(unitId: string) {
  return !!trackedUnitColors.value[unitId];
}

function trackedUnitButtonStyle(unitId: string) {
  const color = trackedUnitColors.value[unitId];
  return color ? { '--track-color': color } : undefined;
}

function setAsTarget() {
  if (!selectionEntry.value || !activeControllableId.value) {
    return;
  }

  gateway.setTacticalTarget(activeControllableId.value, selectionEntry.value.id);
}

function collectResources() {
  if (!selectionEntry.value || !activeControllableId.value) {
    return;
  }

  gateway.collectResources(activeControllableId.value, selectionEntry.value.id);
}

function isResourceCollectionTargetKind(kind: string) {
  const normalized = kind.trim().toLowerCase();
  return normalized === 'planet' || normalized === 'sun' || normalized === 'star' || normalized === 'start';
}

function readSubsystemEntries(value: unknown): ShipSubsystemEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      id: readText(entry.id, readText(entry.slot, 'subsystem')),
      name: normalizeSubsystemToken(readText(entry.name, readText(entry.slot, 'subsystem'))),
      status: humanizeToken(readText(entry.status, 'off')),
      stats: readSubsystemStats(entry.stats),
    }));
}

function readSubsystemStats(value: unknown): ShipSubsystemStat[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      label: readText(entry.label, ''),
      value: readText(entry.value, ''),
    }))
    .filter((entry) => entry.label.length > 0);
}

function readResourceMinerState(subsystems: ShipSubsystemEntry[]): ResourceMinerState | null {
  const miner = subsystems.find((entry) => normalizeSubsystemToken(entry.name) === 'resourceminer'
    || normalizeSubsystemToken(entry.id) === 'resourceminer');
  if (!miner) {
    return null;
  }

  const rate = miner.stats.find((stat) => normalizeSubsystemToken(stat.label) === 'rate')?.value ?? '';
  const yieldValue = miner.stats.find((stat) => normalizeSubsystemToken(stat.label) === 'yield')?.value ?? '';

  return {
    status: miner.status,
    rate,
    yield: yieldValue,
  };
}

function readText(value: unknown, fallback = '') {
  return typeof value === 'string' ? value : fallback;
}

function normalizeSubsystemToken(value: string) {
  return value.toLowerCase().replace(/[\s_-]+/g, '');
}

function humanizeToken(value: string) {
  const normalized = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();

  if (!normalized) {
    return 'Unknown';
  }

  return normalized.charAt(0).toUpperCase() + normalized.slice(1);
}
</script>

<template>
  <aside class="overlay-column overlay-column-right">
    <section v-if="selectionEntry" class="panel selection-unit-card stack">
      <div class="stack">
        <div class="actions-tight overlay-badge-row">
          <span v-for="badge in selectionEntry.badges" :key="badge.label" class="overlay-badge" :class="`is-${badge.tone}`">{{ badge.label }}</span>
        </div>
        <div>
          <h2 class="panel-title">{{ selectionEntry.displayName }}</h2>
          <p class="panel-copy">{{ selectionEntry.kind }} · {{ selectionEntry.clusterLabel }}</p>
        </div>
      </div>

      <dl class="overlay-stat-grid selection-unit-grid">
        <div v-for="stat in selectionEntry.stats" :key="stat.label">
          <dt>{{ stat.label }}</dt>
          <dd>{{ stat.value }}</dd>
        </div>
      </dl>

      <div class="selection-unit-gauges">
        <GaugeMeter v-for="meter in selectionEntry.meters" :key="meter.label" :meter="meter" />
      </div>

      <div class="selection-scan-actions">
        <div class="actions-tight">
          <button class="button-secondary button-compact" type="button" :disabled="!canInitiateTargetedScan" @click="initiateTargetedScan">
            Initiate Scan
          </button>
          <button
            class="button-secondary button-compact"
            :class="{ 'is-active': isUnitTracked(selectionEntry.id) }"
            :style="trackedUnitButtonStyle(selectionEntry.id)"
            type="button"
            @click="toggleTrackedSelection"
          >
            {{ isUnitTracked(selectionEntry.id) ? 'Tracked' : 'Track' }}
          </button>
          <button
            v-if="tacticalMode === 'target'"
            class="button-secondary button-compact"
            type="button"
            :disabled="!canSetAsTarget"
            @click="setAsTarget"
          >
            Set as Target
          </button>
          <button
            class="button-secondary button-compact"
            type="button"
            :disabled="!canCollectResources"
            @click="collectResources"
          >
            Collect Resources
          </button>
        </div>
        <span v-if="selectionRuntimeStatus" class="selection-runtime-status">{{ selectionRuntimeStatus }}</span>
      </div>

      <section v-for="group in selectionEntry.detailGroups" :key="group.title" class="selection-detail-group" :class="`tone-${group.tone}`">
        <h3>{{ group.title }}</h3>
        <dl class="overlay-stat-grid">
          <div v-for="stat in group.stats" :key="stat.label">
            <dt>{{ stat.label }}</dt>
            <dd>{{ stat.value }}</dd>
          </div>
        </dl>
      </section>
    </section>
  </aside>
</template>
