export interface NavigationOverlayPoint {
  x: number;
  y: number;
}

export interface NavigationOverlaySegment {
  startX: number;
  startY: number;
  endX: number;
  endY: number;
}

export interface NavigationOverlayCircle {
  x: number;
  y: number;
  radius: number;
  kind?: string;
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
  searchNodes?: NavigationOverlayPoint[];
  searchEdges?: NavigationOverlaySegment[];
  inflatedObstacles?: NavigationOverlayCircle[];
  trajectory?: NavigationOverlayPoint[];
}
