export interface NavigationOverlayPoint {
  x: number;
  y: number;
}

export interface NavigationOverlayState {
  active: boolean;
  pathStatus?: string;
  targetX?: number;
  targetY?: number;
  pendingTargetX?: number;
  pendingTargetY?: number;
  vectorX?: number;
  vectorY?: number;
  pointerX?: number;
  pointerY?: number;
  path?: NavigationOverlayPoint[];
  trajectory?: NavigationOverlayPoint[];
}
