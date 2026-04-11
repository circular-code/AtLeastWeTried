export function clamp01(value) {
    return Math.max(0, Math.min(1, value));
}
export function magnitude(x, y) {
    return Math.hypot(x, y);
}
/** Connector gravity values are often ~1e-4–1e-2; show enough precision to be readable. */
export function formatGravity(value) {
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
export function formatMetric(value) {
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
export function formatWholeValue(value) {
    return formatMetric(typeof value === 'number' && Number.isFinite(value) ? value : 0);
}
export function formatAngle(angle) {
    const normalized = ((angle % 360) + 360) % 360;
    return `${Math.round(normalized)}°`;
}
export function humanizeCode(value) {
    return value
        .replace(/[_-]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim()
        .replace(/(^|\s)\S/g, (match) => match.toUpperCase());
}
export function truncateText(value, maxLength) {
    if (value.length <= maxLength) {
        return value;
    }
    return `${value.slice(0, maxLength - 1)}…`;
}
export function formatActivityTime(createdAt) {
    return new Intl.DateTimeFormat(undefined, {
        hour: '2-digit',
        minute: '2-digit',
    }).format(createdAt);
}
export function formatDebugTime(createdAt) {
    return new Intl.DateTimeFormat(undefined, {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
    }).format(createdAt);
}
export function buildStatusMeta(message) {
    return message.recoverable ? `${message.kind} · recoverable` : `${message.kind} · requires attention`;
}
export function statusKindToTone(kind) {
    switch (kind) {
        case 'error':
            return 'error';
        case 'warning':
            return 'warn';
        default:
            return 'info';
    }
}
export function formatDebugPayload(message) {
    try {
        return JSON.stringify(message, null, 2);
    }
    catch {
        return '[unserializable message]';
    }
}
export function formatDebugDisplayPayload(payload) {
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
export function formatDebugSnapshotFileName(createdAt) {
    return new Date(createdAt).toISOString().replace(/[:.]/g, '-');
}
export function downloadTextFile(fileName, content) {
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
export function formatCommandReplyDetail(message, getControllableLabel) {
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
