import type {
  AttachConnectionMessage,
  ChatCommandMessage,
  ChatScope,
  ClearNavigationTargetCommandMessage,
  ContinueShipCommandMessage,
  CreateShipCommandMessage,
  DestroyShipCommandMessage,
  DetachPlayerSessionMessage,
  FireWeaponCommandMessage,
  RemoveShipCommandMessage,
  ScannerCommandMessage,
  SelectPlayerSessionMessage,
  SetEngineCommandMessage,
  SetNavigationTargetCommandMessage,
  SetSubsystemModeCommandMessage,
  ShipClass,
} from '../types/generated';

export type CommandEnvelope<TMessage> = {
  commandId: string;
  message: TMessage;
};

export type SetTacticalModeCommandMessage = {
  type: 'command.set_tactical_mode';
  commandId: string;
  payload: {
    controllableId: string;
    mode: 'enemy' | 'target' | 'off';
  };
};

export type SetTacticalTargetCommandMessage = {
  type: 'command.set_tactical_target';
  commandId: string;
  payload: {
    controllableId: string;
    targetId: string;
  };
};

export type ClearTacticalTargetCommandMessage = {
  type: 'command.clear_tactical_target';
  commandId: string;
  payload: {
    controllableId: string;
  };
};

export type UpgradeSubsystemCommandMessage = {
  type: 'command.upgrade_subsystem';
  commandId: string;
  payload: {
    controllableId: string;
    subsystemId: string;
  };
};

export function buildAttachConnectionMessage(apiKey: string, teamName?: string): AttachConnectionMessage {
  return {
    type: 'connection.attach',
    payload: {
      apiKey,
      teamName,
    },
  };
}

export function buildDetachPlayerSessionMessage(playerSessionId: string): DetachPlayerSessionMessage {
  return {
    type: 'connection.detach',
    playerSessionId,
  };
}

export function buildSelectPlayerSessionMessage(playerSessionId: string): SelectPlayerSessionMessage {
  return {
    type: 'player.select',
    playerSessionId,
  };
}

export function buildChatCommand(messageText: string, scope: ChatScope = 'galaxy', recipientPlayerSessionId?: string): CommandEnvelope<ChatCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.chat',
      commandId,
      payload: {
        scope,
        message: messageText,
        recipientPlayerSessionId,
      },
    },
  };
}

export function buildCreateShipCommand(name: string, shipClass: ShipClass, crystalNames: string[] = []): CommandEnvelope<CreateShipCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.create_ship',
      commandId,
      payload: {
        name,
        shipClass,
        crystalNames,
      },
    },
  };
}

export function buildDestroyShipCommand(controllableId: string): CommandEnvelope<DestroyShipCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.destroy_ship',
      commandId,
      payload: { controllableId },
    },
  };
}

export function buildContinueShipCommand(controllableId: string): CommandEnvelope<ContinueShipCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.continue_ship',
      commandId,
      payload: { controllableId },
    },
  };
}

export function buildRemoveShipCommand(controllableId: string): CommandEnvelope<RemoveShipCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.remove_ship',
      commandId,
      payload: { controllableId },
    },
  };
}

export function buildSetEngineCommand(controllableId: string, engineId: string, thrust: number, x?: number, y?: number): CommandEnvelope<SetEngineCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.set_engine',
      commandId,
      payload: {
        controllableId,
        engineId,
        thrust,
        x,
        y,
      },
    },
  };
}

export function buildScannerCommand(controllableId: string, mode?: string, width?: number, length?: number): CommandEnvelope<ScannerCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.scanner',
      commandId,
      payload: {
        controllableId,
        mode,
        width,
        length,
      },
    },
  };
}

export function buildSetNavigationTargetCommand(
  controllableId: string,
  targetX: number,
  targetY: number,
  thrustPercentage?: number,
  direct?: boolean,
): CommandEnvelope<SetNavigationTargetCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.set_navigation_target',
      commandId,
      payload: {
        controllableId,
        targetX,
        targetY,
        thrustPercentage,
        direct: direct ?? false,
      },
    },
  };
}

export function buildClearNavigationTargetCommand(controllableId: string): CommandEnvelope<ClearNavigationTargetCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.clear_navigation_target',
      commandId,
      payload: { controllableId },
    },
  };
}

export function buildFireWeaponCommand(
  controllableId: string,
  weaponId: string,
  relativeAngle?: number,
  targetId?: string,
  targetX?: number,
  targetY?: number,
): CommandEnvelope<FireWeaponCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.fire_weapon',
      commandId,
      payload: {
        controllableId,
        weaponId,
        relativeAngle,
        targetId,
        targetX,
        targetY,
      },
    },
  };
}

export function buildSetSubsystemModeCommand(
  controllableId: string,
  subsystemId: string,
  mode: string,
  value?: number,
  targetId?: string,
  width?: number,
  length?: number,
): CommandEnvelope<SetSubsystemModeCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.set_subsystem_mode',
      commandId,
      payload: {
        controllableId,
        subsystemId,
        mode,
        value,
        targetId,
        width,
        length,
      },
    },
  };
}

export function buildUpgradeSubsystemCommand(controllableId: string, subsystemId: string): CommandEnvelope<UpgradeSubsystemCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.upgrade_subsystem',
      commandId,
      payload: {
        controllableId,
        subsystemId,
      },
    },
  };
}

export function buildSetTacticalModeCommand(
  controllableId: string,
  mode: 'enemy' | 'target' | 'off',
): CommandEnvelope<SetTacticalModeCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.set_tactical_mode',
      commandId,
      payload: {
        controllableId,
        mode,
      },
    },
  };
}

export function buildSetTacticalTargetCommand(controllableId: string, targetId: string): CommandEnvelope<SetTacticalTargetCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.set_tactical_target',
      commandId,
      payload: {
        controllableId,
        targetId,
      },
    },
  };
}

export function buildClearTacticalTargetCommand(controllableId: string): CommandEnvelope<ClearTacticalTargetCommandMessage> {
  const commandId = createCommandId();
  return {
    commandId,
    message: {
      type: 'command.clear_tactical_target',
      commandId,
      payload: {
        controllableId,
      },
    },
  };
}

function createCommandId() {
  return globalThis.crypto?.randomUUID?.() ?? `cmd-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
