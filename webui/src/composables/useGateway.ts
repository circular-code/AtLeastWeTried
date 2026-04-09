import { computed, ref } from 'vue';
import { buildAttachConnectionMessage, buildChatCommand, buildClearNavigationTargetCommand, buildContinueShipCommand, buildCreateShipCommand, buildDestroyShipCommand, buildDetachPlayerSessionMessage, buildFireWeaponCommand, buildRemoveShipCommand, buildScannerCommand, buildSelectPlayerSessionMessage, buildSetEngineCommand, buildSetNavigationTargetCommand } from '../transport/commands';
import { createGatewayClient } from '../transport/gateway';
import { loadSavedConnections, removeSavedConnection, upsertSavedConnection } from '../lib/savedConnections';
import { truncateText } from '../lib/formatting';
import { isValidPlayerApiKey } from '../lib/validation';
import { useGameStore } from '../stores/game';
import { useSessionStore } from '../stores/session';
import { useUiStore } from '../stores/ui';
import type { PendingAttachment, SavedGatewayConnection, ScannerMode, ShipCreateRequest, TacticalMode } from '../types/client';
import type { ServerMessage } from '../types/generated';

type GatewayApi = ReturnType<typeof createGatewayApi>;

let gatewayApi: GatewayApi | null = null;

export function useGateway() {
  if (!gatewayApi) {
    gatewayApi = createGatewayApi();
  }

  return gatewayApi;
}

function createGatewayApi() {
  const sessionStore = useSessionStore();
  const gameStore = useGameStore();
  const uiStore = useUiStore();
  const savedConnections = ref<SavedGatewayConnection[]>(loadSavedConnections());
  const pendingAttachments = ref<PendingAttachment[]>([]);
  let initialized = false;

  const client = createGatewayClient(sessionStore.gatewayUrl, {
    onStateChange(nextState) {
      sessionStore.setConnectionState(nextState);

      if (nextState === 'open') {
        flushPendingAttachments();
        return;
      }

      if (nextState === 'closed' || nextState === 'error') {
        sessionStore.clearSession();
        gameStore.clearOverlay();
      }
    },
    onSend(message) {
      uiStore.recordDebugMessage('client', message);
    },
    onMessage(message) {
      uiStore.recordDebugMessage('server', message);
      applyServerMessage(message);
    },
  });

  function initialize() {
    if (initialized) {
      return;
    }

    initialized = true;
    uiStore.restorePreferences();

    if (savedConnections.value.length > 0) {
      connect();
    }
  }

  function applyServerMessage(message: ServerMessage) {
    switch (message.type) {
      case 'session.ready':
        sessionStore.applySessionReady(message);
        gameStore.clearOverlay();
        uiStore.setSelectedControllable('');
        break;
      case 'snapshot.full':
        gameStore.applySnapshot(message.snapshot);
        break;
      case 'world.delta':
        gameStore.applyWorldDelta(message);
        break;
      case 'owner.delta':
        gameStore.applyOwnerDelta(message);
        break;
      case 'chat.received':
        gameStore.addChatEntry(message.entry);
        break;
      case 'command.reply': {
        const selectedControllableId = gameStore.resolveCommand(message);
        if (selectedControllableId) {
          uiStore.setSelectedControllable(selectedControllableId);
        }
        break;
      }
      case 'status':
        gameStore.recordStatus(message);
        break;
      case 'ping':
        break;
    }
  }

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

  function disconnect() {
    client.disconnect();
  }

  function addConnection(apiKey: string, teamName: string, remember = true) {
    const normalizedApiKey = apiKey.trim();
    if (!normalizedApiKey) {
      return;
    }

    if (!isValidPlayerApiKey(normalizedApiKey)) {
      gameStore.recordActivity({
        tone: 'warn',
        summary: 'Invalid Player API key',
        detail: 'Enter a 64-character hexadecimal Flattiverse Player API key.',
        meta: 'local validation',
      });
      return;
    }

    if (remember) {
      savedConnections.value = upsertSavedConnection(normalizedApiKey, teamName);
    }

    queueAttachment(normalizedApiKey, teamName);
  }

  function attachSavedConnection(savedConnection: SavedGatewayConnection) {
    queueAttachment(savedConnection.apiKey, savedConnection.teamName);
  }

  function forgetSavedConnection(id: string) {
    savedConnections.value = removeSavedConnection(id);
  }

  function queueAttachment(apiKey: string, teamName: string) {
    if (!isValidPlayerApiKey(apiKey)) {
      gameStore.recordActivity({
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

    if (client.isOpen()) {
      flushPendingAttachments();
      return;
    }

    client.connect();
  }

  function flushPendingAttachments() {
    if (!client.isOpen() || pendingAttachments.value.length === 0) {
      return;
    }

    const seenKeys = new Set<string>();

    for (const attachment of pendingAttachments.value) {
      const normalizedKey = attachment.apiKey.trim().toLowerCase();
      if (!normalizedKey || seenKeys.has(normalizedKey)) {
        continue;
      }

      seenKeys.add(normalizedKey);
      client.send(buildAttachConnectionMessage(attachment.apiKey.trim(), attachment.teamName.trim() || undefined));
    }

    pendingAttachments.value = [];
  }

  function selectPlayerSession(playerSessionId: string) {
    if (!playerSessionId) {
      return;
    }

    gameStore.clearOverlay();
    uiStore.setSelectedControllable('');
    client.send(buildSelectPlayerSessionMessage(playerSessionId));
  }

  function detachPlayerSession(playerSessionId: string) {
    if (!playerSessionId) {
      return;
    }

    client.send(buildDetachPlayerSessionMessage(playerSessionId));
  }

  function sendChat(messageText: string) {
    const normalizedMessage = messageText.trim();
    if (!normalizedMessage) {
      return;
    }

    const envelope = buildChatCommand(normalizedMessage, 'galaxy');
    gameStore.trackCommand(envelope.commandId, {
      label: 'Transmit galaxy chat',
      subject: truncateText(normalizedMessage, 72),
    });
    client.send(envelope.message);
  }

  function createShip(request: ShipCreateRequest) {
    const shipName = request.name.trim() || 'Prototype Wing';
    const envelope = buildCreateShipCommand(shipName, request.shipClass, []);

    gameStore.trackCommand(envelope.commandId, {
      label: `Deploy ${request.shipClass} ship`,
      subject: shipName,
    });

    client.send(envelope.message);
  }

  function destroyShip(controllableId: string) {
    if (!controllableId) {
      return;
    }

    const envelope = buildDestroyShipCommand(controllableId);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Destroy ship',
      subject: gameStore.getControllableLabel(controllableId),
    });
    client.send(envelope.message);
  }

  function continueShip(controllableId: string) {
    if (!controllableId) {
      return;
    }

    const envelope = buildContinueShipCommand(controllableId);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Spawn ship',
      subject: gameStore.getControllableLabel(controllableId),
    });
    client.send(envelope.message);
  }

  function removeShip(controllableId: string) {
    if (!controllableId) {
      return;
    }

    const envelope = buildRemoveShipCommand(controllableId);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Remove ship',
      subject: gameStore.getControllableLabel(controllableId),
    });
    client.send(envelope.message);
  }

  function setEngine(controllableId: string, thrust: number) {
    if (!controllableId) {
      return;
    }

    const envelope = buildSetEngineCommand(controllableId, 'main_engine', thrust);
    gameStore.trackCommand(envelope.commandId, {
      label: `Set thrust to ${thrust.toFixed(2)}`,
      subject: gameStore.getControllableLabel(controllableId),
    });
    client.send(envelope.message);
  }

  function fireWeapon(controllableId: string) {
    if (!controllableId) {
      return;
    }

    const envelope = buildFireWeaponCommand(controllableId, 'main_weapon', 0);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Fire weapon',
      subject: gameStore.getControllableLabel(controllableId),
    });
    client.send(envelope.message);
  }

  function setScannerMode(controllableId: string, mode: ScannerMode) {
    if (!controllableId) {
      return;
    }

    const envelope = buildScannerCommand(controllableId, mode, gameStore.scannerWidthFor(controllableId));

    gameStore.trackCommand(envelope.commandId, {
      label: `Scanner ${mode}`,
      subject: gameStore.getControllableLabel(controllableId),
    });

    client.send(envelope.message);
  }

  function setScannerWidth(controllableId: string, width: number) {
    if (!controllableId) {
      return;
    }

    const envelope = buildScannerCommand(controllableId, undefined, width);

    gameStore.trackCommand(envelope.commandId, {
      label: `Scanner width ${width.toFixed(0)}°`,
      subject: gameStore.getControllableLabel(controllableId),
    });

    client.send(envelope.message);
  }

  function setTacticalMode(controllableId: string, mode: TacticalMode) {
    if (!controllableId || mode === 'off') {
      return;
    }

    const envelope = buildFireWeaponCommand(controllableId, 'shot', 0);
    gameStore.trackCommand(envelope.commandId, {
      label: `Tactical ${mode}`,
      subject: gameStore.getControllableLabel(controllableId),
    });

    client.send(envelope.message);
  }

  function setNavigationTarget(controllableId: string, worldX: number, worldY: number, thrustPercentage: number) {
    if (!controllableId) {
      gameStore.recordActivity({
        tone: 'warn',
        summary: 'No active ship selected',
        detail: 'Select one of your controllables before setting a navigation target.',
        meta: 'navigation target',
      });
      return;
    }

    const envelope = buildSetNavigationTargetCommand(controllableId, worldX, -worldY, thrustPercentage);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Set navigation target',
      subject: `${gameStore.getControllableLabel(controllableId)} -> ${truncateText(`${worldX.toFixed(1)}, ${(-worldY).toFixed(1)}`, 42)} @ ${thrustPercentage.toFixed(2)}`,
    });

    client.send(envelope.message);
  }

  function clearNavigationTarget(controllableId: string) {
    if (!controllableId) {
      return;
    }

    gameStore.clearNavigationOverlay(controllableId);
    const envelope = buildClearNavigationTargetCommand(controllableId);
    gameStore.trackCommand(envelope.commandId, {
      label: 'Clear navigation target',
      subject: gameStore.getControllableLabel(controllableId),
    });

    client.send(envelope.message);
  }

  return {
    savedConnections,
    pendingAttachments,
    pendingAttachmentsCount: computed(() => pendingAttachments.value.length),
    initialize,
    connect,
    disconnect,
    addConnection,
    attachSavedConnection,
    forgetSavedConnection,
    selectPlayerSession,
    detachPlayerSession,
    sendChat,
    createShip,
    destroyShip,
    continueShip,
    removeShip,
    setEngine,
    fireWeapon,
    setScannerMode,
    setScannerWidth,
    setTacticalMode,
    setNavigationTarget,
    clearNavigationTarget,
  };
}