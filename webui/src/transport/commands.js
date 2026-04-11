export function buildAttachConnectionMessage(apiKey, teamName) {
    return {
        type: 'connection.attach',
        payload: {
            apiKey,
            teamName,
        },
    };
}
export function buildDetachPlayerSessionMessage(playerSessionId) {
    return {
        type: 'connection.detach',
        playerSessionId,
    };
}
export function buildSelectPlayerSessionMessage(playerSessionId) {
    return {
        type: 'player.select',
        playerSessionId,
    };
}
export function buildChatCommand(messageText, scope = 'galaxy', recipientPlayerSessionId) {
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
export function buildCreateShipCommand(name, shipClass, crystalNames = []) {
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
export function buildDestroyShipCommand(controllableId) {
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
export function buildContinueShipCommand(controllableId) {
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
export function buildRemoveShipCommand(controllableId) {
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
export function buildSetEngineCommand(controllableId, engineId, thrust, x, y) {
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
export function buildScannerCommand(controllableId, mode, width, length) {
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
export function buildSetNavigationTargetCommand(controllableId, targetX, targetY, thrustPercentage, direct) {
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
export function buildClearNavigationTargetCommand(controllableId) {
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
export function buildFireWeaponCommand(controllableId, weaponId, relativeAngle, targetId, targetX, targetY) {
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
export function buildSetSubsystemModeCommand(controllableId, subsystemId, mode, value, targetId, width, length) {
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
export function buildUpgradeSubsystemCommand(controllableId, subsystemId) {
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
export function buildSetTacticalModeCommand(controllableId, mode) {
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
export function buildSetTacticalTargetCommand(controllableId, targetId) {
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
export function buildClearTacticalTargetCommand(controllableId) {
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
