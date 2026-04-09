import * as THREE from 'three';
import type { NormalizedUnit } from './unitBody';
import { TRACE_OPACITY_SCALE, UNIT_KIND_CODE, UNSEEN_OPACITY_SCALE } from './constants';

export type UnitVisual = {
  heading?: THREE.Line;
  instanceIndex: number;
};

const unseenDynamicTint = new THREE.Color('#90a2b5');
const unseenDynamicAccent = new THREE.Color('#7fc9ff');

export function normalizeKind(kind: string): string {
  const lower = kind.toLowerCase();
  if (lower.includes('classicshipplayerunit')) return 'classic-ship';
  if (lower.includes('modernshipplayerunit')) return 'modern-ship';
  if (lower.includes('ship')) return 'ship';
  if (lower.includes('planet')) return 'planet';
  if (lower.includes('moon')) return 'moon';
  if (lower.includes('meteoroid')) return 'meteoroid';
  if (lower.includes('wormhole')) return 'wormhole';
  if (lower.includes('gate')) return 'gate';
  if (lower.includes('shot')) return 'shot';
  if (lower.includes('rail')) return 'rail';
  return lower;
}

export function getSizeForKind(kind: string): number {
  switch (kind) {
    case 'sun': return 22;
    case 'planet': return 14;
    case 'moon': return 9;
    case 'wormhole':
    case 'gate': return 12;
    case 'classic-ship': return 9.5;
    case 'modern-ship':
    case 'ship': return 8.5;
    case 'shot':
    case 'rail': return 2.2;
    default: return 6;
  }
}

export function getBodyExtent(kind: string): number {
  switch (kind) {
    case 'wormhole':
    case 'gate': return 0.78;
    case 'shot': return 0.66;
    case 'rail': return 0.82;
    default: return 0.7;
  }
}

export function getFallbackRenderRadius(kind: string): number {
  return getSizeForKind(kind) * getBodyExtent(kind);
}

export function getRenderRadius(radius: number | null | undefined, kind: string): number {
  return typeof radius === 'number' && Number.isFinite(radius) && radius > 0
    ? radius
    : getFallbackRenderRadius(kind);
}

export function getRenderScale(unit: NormalizedUnit): number {
  return unit.renderRadius / getBodyExtent(unit.renderKind);
}

export function isUnseenDynamicUnit(unit: NormalizedUnit): boolean {
  return !unit.isStatic && !unit.isSeen;
}

function getBaseOpacityForKind(kind: string): number {
  if (kind === 'wormhole' || kind === 'gate') return 0.82;
  if (kind === 'shot' || kind === 'rail') return 0.9;
  return 1;
}

export function getOpacityForUnit(unit: NormalizedUnit): number {
  let baseOpacity = getBaseOpacityForKind(unit.renderKind);

  if (unit.isTrace) return baseOpacity * TRACE_OPACITY_SCALE;
  else if (isUnseenDynamicUnit(unit)) return baseOpacity * UNSEEN_OPACITY_SCALE;
  return baseOpacity;
}

export function getShaderKindCode(kind: string): number {
  return UNIT_KIND_CODE[kind] ?? UNIT_KIND_CODE.default;
}

export function toSceneRotation(angleDegrees: number): number {
  return -THREE.MathUtils.degToRad(angleDegrees);
}

export function isDirectionalKind(kind: string): boolean {
  return kind === 'shot' || kind === 'rail';
}

export function isShipKind(kind: string): boolean {
  return kind === 'classic-ship' || kind === 'modern-ship' || kind === 'ship';
}

export function getColorForUnit(unit: NormalizedUnit, teamColors: Map<string, string>): string {
  let baseColor: string;
  if (unit.teamName && teamColors.has(unit.teamName)) {
    baseColor = teamColors.get(unit.teamName)!;
  } else {
    switch (unit.renderKind) {
      case 'sun': baseColor = '#ffb347'; break;
      case 'planet': baseColor = '#7dd3fc'; break;
      case 'moon': baseColor = '#c9d8ff'; break;
      case 'wormhole': baseColor = '#7aebff'; break;
      case 'gate': baseColor = '#90f1b8'; break;
      case 'shot': baseColor = '#ffd369'; break;
      case 'rail': baseColor = '#ff8a5b'; break;
      default: baseColor = '#d7ecff'; break;
    }
  }

  if (!isUnseenDynamicUnit(unit)) {
    return baseColor;
  }

  const faded = new THREE.Color(baseColor)
    .lerp(unseenDynamicTint, 0.72)
    .lerp(unseenDynamicAccent, 0.16);
  return `#${faded.getHexString()}`;
}

export function createShipArrowPoints(): THREE.Vector3[] {
  return [
    new THREE.Vector3(-0.42, 0, 0),
    new THREE.Vector3(0.34, 0, 0),
    new THREE.Vector3(0.08, 0.2, 0),
    new THREE.Vector3(0.34, 0, 0),
    new THREE.Vector3(0.08, -0.2, 0),
  ];
}

export function getHeadingPoints(kind: string): THREE.Vector3[] {
  if (isShipKind(kind)) {
    return createShipArrowPoints();
  }

  return [new THREE.Vector3(0, 0, 0), new THREE.Vector3(1, 0, 0)];
}

export function shouldPersistTraceForUnit(unit: NormalizedUnit): boolean {
  return isShipKind(unit.renderKind) && !unit.isTrace;
}
