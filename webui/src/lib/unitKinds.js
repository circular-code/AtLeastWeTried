export function canonicalUnitKind(kind) {
    return kind
        .trim()
        .replace(/[\s_-]+/g, '')
        .toLowerCase();
}
export function isShortLivedProjectileKind(kind) {
    switch (canonicalUnitKind(kind)) {
        case 'shot':
        case 'interceptor':
        case 'missile':
        case 'projectile':
        case 'rail':
            return true;
        default:
            return false;
    }
}
