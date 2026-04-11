import type { GatewayMessageDirection, ShipClass } from './generated';

export type ControllableOverlayState = Record<string, unknown>;

export type ActivityTone = 'info' | 'success' | 'warn' | 'error';

export type OverlayBadgeTone = 'accent' | 'ok' | 'muted' | 'warn';

export type OverlayMeterTone = 'energy' | 'hull' | 'shield';

export type ScannerMode = '360' | 'forward' | 'hold' | 'sweep' | 'targeted' | 'off';

export type TacticalMode = 'enemy' | 'target' | 'scan' | 'off';

export interface SavedGatewayConnection {
  id: string;
  label: string;
  apiKey: string;
  teamName: string;
}

export interface PendingAttachment {
  apiKey: string;
  teamName: string;
}

export interface PendingCommandDescriptor {
  label: string;
  subject?: string;
}

export interface ActivityEntry {
  id: string;
  tone: ActivityTone;
  summary: string;
  detail?: string;
  meta?: string;
  createdAt: number;
}

export interface DebugLogEntry {
  id: string;
  direction: GatewayMessageDirection;
  messageType: string;
  payload: string;
  createdAt: number;
}

export interface OverlayBadge {
  label: string;
  tone: OverlayBadgeTone;
}

export interface OverlayMeter {
  label: string;
  ratio: number;
  valueText: string;
  tone: OverlayMeterTone;
  inactive?: boolean;
}

export interface OverlayStat {
  label: string;
  value: string;
}

export interface OverlayDetailGroup {
  title: string;
  tone: 'solar' | 'hazard' | 'tech';
  stats: OverlayStat[];
}

export interface OwnedControllableSummary {
  controllableId: string;
  displayName: string;
  teamName: string;
  alive: boolean;
  score: number;
  kind: string;
}

export interface OverlayEntry {
  id: string;
  displayName: string;
  kind: string;
  alive: boolean;
  clusterLabel: string;
  badges: OverlayBadge[];
  statusLabel?: string;
  meters: OverlayMeter[];
  stats: OverlayStat[];
}

export interface ClickedUnitEntry extends OverlayEntry {
  positionLabel: string;
  detailGroups: OverlayDetailGroup[];
}

export interface ShipCreateRequest {
  name: string;
  shipClass: ShipClass;
}
