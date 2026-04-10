import type { CommandReplyMessage, ClientMessage, ServerMessage, ServerStatusMessage } from '../types/generated';
import type { ClearTacticalTargetCommandMessage, SetTacticalModeCommandMessage, SetTacticalTargetCommandMessage, UpgradeSubsystemCommandMessage } from '../transport/commands';

type DebugGatewayMessage = ClientMessage | ServerMessage | SetTacticalModeCommandMessage | SetTacticalTargetCommandMessage | ClearTacticalTargetCommandMessage | UpgradeSubsystemCommandMessage;

export function clamp01(value: number) {
  return Math.max(0, Math.min(1, value));
}

export function magnitude(x: number, y: number) {
  return Math.hypot(x, y);
}

/** Connector gravity values are often ~1e-4–1e-2; show enough precision to be readable. */
export function formatGravity(value: number) {
  if (!Number.isFinite(value)) {
    return '—';
  }

  if (value === 0) {
    return '0';
  }

  const absolute = Math.abs(value);
  if (absolute < 0.0001) {
    return value.toExponential(2);
  }

  return value.toFixed(4).replace(/\.?0+$/, '');
}

export function formatMetric(value: number) {
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

export function formatWholeValue(value: unknown) {
  return formatMetric(typeof value === 'number' && Number.isFinite(value) ? value : 0);
}

export function formatAngle(angle: number) {
  const normalized = ((angle % 360) + 360) % 360;
  return `${Math.round(normalized)}°`;
}

export function humanizeCode(value: string) {
  return value
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/(^|\s)\S/g, (match) => match.toUpperCase());
}

export function truncateText(value: string, maxLength: number) {
  if (value.length <= maxLength) {
    return value;
  }

  return `${value.slice(0, maxLength - 1)}…`;
}

export function formatActivityTime(createdAt: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
  }).format(createdAt);
}

export function formatDebugTime(createdAt: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(createdAt);
}

export function buildStatusMeta(message: ServerStatusMessage) {
  return message.recoverable ? `${message.kind} · recoverable` : `${message.kind} · requires attention`;
}

export function statusKindToTone(kind: ServerStatusMessage['kind']) {
  switch (kind) {
    case 'error':
      return 'error' as const;
    case 'warning':
      return 'warn' as const;
    default:
      return 'info' as const;
  }
}

export function formatDebugPayload(message: DebugGatewayMessage) {
  try {
    return JSON.stringify(message, null, 2);
  } catch {
    return '[unserializable message]';
  }
}

export function formatDebugDisplayPayload(payload: string) {
  const trimmed = payload.trim();
  if (!trimmed.startsWith('{') || !trimmed.endsWith('}')) {
    return payload;
  }

  const inner = trimmed.slice(1, -1).trim();
  if (!inner) {
    return '';
  }

  return inner.replace(/^  /gm, '');
}

export function formatDebugSnapshotFileName(createdAt: number) {
  return new Date(createdAt).toISOString().replace(/[:.]/g, '-');
}

export function downloadTextFile(fileName: string, content: string) {
  const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');

  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

export function formatCommandReplyDetail(
  message: CommandReplyMessage,
  getControllableLabel: (controllableId: string) => string,
) {
  if (message.error) {
    return message.error.message;
  }

  const result = message.result ?? {};
  const action = typeof result.action === 'string' ? result.action : undefined;
  const controllableId = typeof result.controllableId === 'string' ? result.controllableId : undefined;
  const mode = typeof result.mode === 'string' ? result.mode : undefined;
  const scope = typeof result.scope === 'string' ? result.scope : undefined;
  const thrustValue = typeof result.thrust === 'number' ? result.thrust : undefined;

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
