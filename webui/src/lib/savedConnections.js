const STORAGE_KEY = 'flattiverse.gateway.savedConnections';
export function loadSavedConnections() {
    try {
        const raw = globalThis.localStorage?.getItem(STORAGE_KEY);
        if (!raw) {
            return [];
        }
        const parsed = JSON.parse(raw);
        if (!Array.isArray(parsed)) {
            return [];
        }
        return parsed.flatMap((item) => {
            if (!item || typeof item !== 'object') {
                return [];
            }
            const candidate = item;
            if (typeof candidate.id !== 'string' || typeof candidate.label !== 'string' || typeof candidate.apiKey !== 'string') {
                return [];
            }
            return [{
                    id: candidate.id,
                    label: candidate.label,
                    apiKey: candidate.apiKey,
                    teamName: typeof candidate.teamName === 'string' ? candidate.teamName : '',
                }];
        });
    }
    catch {
        return [];
    }
}
export function upsertSavedConnection(apiKey, teamName = '') {
    const current = loadSavedConnections();
    const normalizedApiKey = apiKey.trim();
    const normalizedTeamName = teamName.trim();
    const existing = current.find((item) => item.apiKey.toLowerCase() === normalizedApiKey.toLowerCase());
    const nextEntry = {
        id: existing?.id ?? (globalThis.crypto?.randomUUID?.() ?? `saved-${Date.now()}-${Math.random().toString(16).slice(2)}`),
        label: existing?.label ?? `Key ${maskApiKey(normalizedApiKey)}`,
        apiKey: normalizedApiKey,
        teamName: normalizedTeamName,
    };
    const next = existing
        ? current.map((item) => (item.id === existing.id ? nextEntry : item))
        : [nextEntry, ...current];
    persist(next);
    return next;
}
export function removeSavedConnection(id) {
    const next = loadSavedConnections().filter((item) => item.id !== id);
    persist(next);
    return next;
}
export function maskApiKey(apiKey) {
    const trimmed = apiKey.trim();
    if (trimmed.length <= 12) {
        return trimmed;
    }
    return `${trimmed.slice(0, 6)}...${trimmed.slice(-4)}`;
}
function persist(connections) {
    globalThis.localStorage?.setItem(STORAGE_KEY, JSON.stringify(connections));
}
