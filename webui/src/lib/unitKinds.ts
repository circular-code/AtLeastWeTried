export function canonicalUnitKind(kind: string): string {
  return kind
    .trim()
    .replace(/[\s_-]+/g, '')
    .toLowerCase();
}

export function isShortLivedProjectileKind(kind: string): boolean {
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
