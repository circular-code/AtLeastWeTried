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
export function isShortLivedExplosionKind(kind) {
    switch (canonicalUnitKind(kind)) {
        case 'explosion':
        case 'interceptorexplosion':
            return true;
        default:
            return false;
    }
}
export function isShortLivedTransientUnitKind(kind) {
    return isShortLivedProjectileKind(kind) || isShortLivedExplosionKind(kind);
}
export function isPlayerShipUnitKind(kind) {
    switch (canonicalUnitKind(kind)) {
        case 'classicship':
        case 'classicshipplayerunit':
        case 'modernship':
        case 'modernshipplayerunit':
            return true;
        default:
            return false;
    }
}
