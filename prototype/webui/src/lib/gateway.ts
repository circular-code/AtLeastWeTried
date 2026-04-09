import type {
  AttachConnectionMessage,
  ChatCommandMessage,
  ChatScope,
  ClearNavigationTargetCommandMessage,
  ClientMessage,
  ContinueShipCommandMessage,
  CreateShipCommandMessage,
  DestroyShipCommandMessage,
  DetachPlayerSessionMessage,
  FireWeaponCommandMessage,
  RemoveShipCommandMessage,
  ServerMessage,
  SetEngineCommandMessage,
  SetNavigationTargetCommandMessage,
  SetSubsystemModeCommandMessage,
  ShipClass,
  SelectPlayerSessionMessage,
} from '../types/generated';

export type ClientConnectionState = 'idle' | 'connecting' | 'open' | 'closed' | 'error';

type Handlers = {
  onStateChange?: (state: ClientConnectionState) => void;
  onMessage?: (message: ServerMessage) => void;
  onSend?: (message: ClientMessage) => void;
};

export function createGatewayClient(url: string, handlers: Handlers) {
  let socket: WebSocket | null = null;

  function setState(state: ClientConnectionState) {
    handlers.onStateChange?.(state);
  }

  function connect() {
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
      return;
    }

    setState('connecting');
    socket = new WebSocket(url);

    socket.addEventListener('open', () => {
      setState('open');
    });

    socket.addEventListener('message', (event) => {
      try {
        const parsed = JSON.parse(event.data) as ServerMessage;
        handlers.onMessage?.(parsed);
      } catch {
        setState('error');
      }
    });

    socket.addEventListener('close', () => {
      setState('closed');
    });

    socket.addEventListener('error', () => {
      setState('error');
    });
  }

  function send(message: ClientMessage) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return;
    }

    handlers.onSend?.(message);
    socket.send(JSON.stringify(message));
  }

  function attachConnection(apiKey: string, teamName?: string) {
    const message: AttachConnectionMessage = {
      type: 'connection.attach',
      payload: {
        apiKey,
        teamName,
      },
    };

    send(message);
  }

  function detachPlayerSession(playerSessionId: string) {
    const message: DetachPlayerSessionMessage = {
      type: 'connection.detach',
      playerSessionId,
    };

    send(message);
  }

  function selectPlayerSession(playerSessionId: string) {
    const message: SelectPlayerSessionMessage = {
      type: 'player.select',
      playerSessionId,
    };

    send(message);
  }

  function sendChat(messageText: string, scope: ChatScope = 'galaxy', recipientPlayerSessionId?: string) {
    const commandId = createCommandId();
    const message: ChatCommandMessage = {
      type: 'command.chat',
      commandId,
      payload: {
        scope,
        message: messageText,
        recipientPlayerSessionId,
      },
    };

    send(message);
    return commandId;
  }

  function createShip(name: string, shipClass: ShipClass, crystalNames: string[] = []) {
    const commandId = createCommandId();
    const message: CreateShipCommandMessage = {
      type: 'command.create_ship',
      commandId,
      payload: {
        name,
        shipClass,
        crystalNames,
      },
    };

    send(message);
    return commandId;
  }

  function setEngine(controllableId: string, engineId: string, thrust: number, x?: number, y?: number) {
    const commandId = createCommandId();
    const message: SetEngineCommandMessage = {
      type: 'command.set_engine',
      commandId,
      payload: {
        controllableId,
        engineId,
        thrust,
        x,
        y,
      },
    };

    send(message);
    return commandId;
  }

  function setNavigationTarget(controllableId: string, targetX: number, targetY: number) {
    const commandId = createCommandId();
    const message: SetNavigationTargetCommandMessage = {
      type: 'command.set_navigation_target',
      commandId,
      payload: {
        controllableId,
        targetX,
        targetY,
      },
    };

    send(message);
    return commandId;
  }

  function clearNavigationTarget(controllableId: string) {
    const commandId = createCommandId();
    const message: ClearNavigationTargetCommandMessage = {
      type: 'command.clear_navigation_target',
      commandId,
      payload: {
        controllableId,
      },
    };

    send(message);
    return commandId;
  }

  function fireWeapon(controllableId: string, weaponId: string, relativeAngle?: number, targetId?: string) {
    const commandId = createCommandId();
    const message: FireWeaponCommandMessage = {
      type: 'command.fire_weapon',
      commandId,
      payload: {
        controllableId,
        weaponId,
        relativeAngle,
        targetId,
      },
    };

    send(message);
    return commandId;
  }

  function setSubsystemMode(controllableId: string, subsystemId: string, mode: string, value?: number, targetId?: string) {
    const commandId = createCommandId();
    const message: SetSubsystemModeCommandMessage = {
      type: 'command.set_subsystem_mode',
      commandId,
      payload: {
        controllableId,
        subsystemId,
        mode,
        value,
        targetId,
      },
    };

    send(message);
    return commandId;
  }

  function destroyShip(controllableId: string) {
    const commandId = createCommandId();
    const message: DestroyShipCommandMessage = {
      type: 'command.destroy_ship',
      commandId,
      payload: {
        controllableId,
      },
    };

    send(message);
    return commandId;
  }

  function continueShip(controllableId: string) {
    const commandId = createCommandId();
    const message: ContinueShipCommandMessage = {
      type: 'command.continue_ship',
      commandId,
      payload: {
        controllableId,
      },
    };

    send(message);
    return commandId;
  }

  function removeShip(controllableId: string) {
    const commandId = createCommandId();
    const message: RemoveShipCommandMessage = {
      type: 'command.remove_ship',
      commandId,
      payload: {
        controllableId,
      },
    };

    send(message);
    return commandId;
  }

  function disconnect() {
    if (!socket) {
      return;
    }

    socket.close();
    socket = null;
  }

  return {
    attachConnection,
    connect,
    detachPlayerSession,
    disconnect,
    send,
    selectPlayerSession,
    sendChat,
    createShip,
    destroyShip,
    continueShip,
    clearNavigationTarget,
    setEngine,
    setNavigationTarget,
    fireWeapon,
    removeShip,
    setSubsystemMode,
  };
}

function createCommandId() {
  return globalThis.crypto?.randomUUID?.() ?? `cmd-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
