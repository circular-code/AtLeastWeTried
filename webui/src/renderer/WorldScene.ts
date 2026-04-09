import * as THREE from 'three';
import type { GalaxySnapshotDto, PublicControllableSnapshotDto, TeamSnapshotDto, UnitSnapshotDto } from '../types/generated';

export type WorldSceneSelection = {
  worldX: number;
  worldY: number;
  unitId?: string;
  kind?: string;
  teamName?: string;
};

type WorldSceneNavigationTarget = {
  x: number;
  y: number;
} | null;

type WorldSceneOptions = {
  onSelection?: (selection: WorldSceneSelection) => void;
  onNavigationTargetRequested?: (selection: WorldSceneSelection) => void;
};

type OwnerOverlayState = Record<string, unknown>;

type UnitVisual = {
  heading?: THREE.Line;
  instanceIndex: number;
};

type NormalizedUnit = UnitSnapshotDto & {
  renderKind: string;
  renderRadius: number;
};

type UnitShaderMaterial = THREE.ShaderMaterial & {
  uniforms: {
    time: THREE.IUniform<number>;
  };
};

type DebugUnit = UnitSnapshotDto & {
  renderKind: string;
  renderRadius: number;
};

type UnitBodyMesh = {
  mesh: THREE.InstancedMesh<THREE.PlaneGeometry, UnitShaderMaterial>;
  colorAttribute: THREE.InstancedBufferAttribute;
  kindAttribute: THREE.InstancedBufferAttribute;
  opacityAttribute: THREE.InstancedBufferAttribute;
};

const DEFAULT_VIEW_HALF_WIDTH = 800;
const DEFAULT_VIEW_HALF_HEIGHT = 450;
const DEBUG_SUN_COUNT = 0;
const DEBUG_SUN_BOUNDS_X = 5000;
const DEBUG_SUN_BOUNDS_Y = 2600;
const MIN_PICK_RADIUS_PX = 12;
const PICK_RADIUS_PADDING_PX = 4;

// --- Scanner Cone Shader ---

const SCANNER_CONE_VERTEX_SHADER = `
  varying vec2 vLocalUv;

  void main() {
    vLocalUv = uv * 2.0 - 1.0;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const SCANNER_CONE_FRAGMENT_SHADER = `
  uniform float time;
  uniform float halfWidthRad;
  uniform float coneLength;
  uniform float targetHalfWidthRad;
  uniform float targetLength;
  uniform vec3 color;
  uniform float opacity;

  varying vec2 vLocalUv;

  const float PI = 3.14159265359;

  void main() {
    // vLocalUv goes -1..+1; the cone points in +X direction (angle=0)
    float dist = length(vLocalUv);
    float angle = atan(vLocalUv.y, vLocalUv.x);

    // Discard behind origin
    if (vLocalUv.x < -0.02) discard;

    float r = length(vLocalUv);
    // Normalize radial distance: 0 at center, 1 at cone range
    float radialNorm = r;

    // Angular mask: inside the current scan cone half-angle
    float absAngle = abs(angle);
    float edgeSoftness = 0.006;
    float angleMask = 1.0 - smoothstep(halfWidthRad - edgeSoftness, halfWidthRad + edgeSoftness, absAngle);

    // Radial mask: hard cutoff at range
    float radialMask = 1.0 - step(1.0, radialNorm);

    // Combine
    float coneMask = angleMask * radialMask;

    // Sweep effect: radial pulse lines
    float sweep = 0.5 + 0.5 * sin(radialNorm * 28.0 - time * 3.0);
    float sweepMask = sweep * 0.3;

    // Inner glow near origin
    float innerGlow = (1.0 - smoothstep(0.0, 0.35, radialNorm)) * 0.4;

    // Edge highlight — thin crisp border along the cone sides
    float edgeInner = smoothstep(halfWidthRad - 0.012, halfWidthRad - 0.004, absAngle);
    float edgeOuter = 1.0 - smoothstep(halfWidthRad - 0.004, halfWidthRad + 0.004, absAngle);
    float edgeGlow = edgeInner * edgeOuter * radialMask * 0.8;

    // Radial arc at the range limit
    float arcEdge = smoothstep(0.96, 0.98, radialNorm) * (1.0 - smoothstep(0.99, 1.0, radialNorm)) * angleMask * 0.7;

    // Target cone outline (faint dashed)
    float targetAngleMask = smoothstep(targetHalfWidthRad - 0.005, targetHalfWidthRad, absAngle)
                          * (1.0 - smoothstep(targetHalfWidthRad, targetHalfWidthRad + 0.008, absAngle));
    float targetRadialEdge = smoothstep(targetLength - 0.01, targetLength, radialNorm)
                           * (1.0 - smoothstep(targetLength, targetLength + 0.008, radialNorm));
    float targetInsideAngle = 1.0 - smoothstep(targetHalfWidthRad - 0.004, targetHalfWidthRad + 0.004, absAngle);
    float dash = step(0.5, fract(radialNorm * 12.0));
    float targetOutline = (targetAngleMask * radialMask + targetRadialEdge * targetInsideAngle) * dash * 0.35;

    float alpha = (coneMask * (0.08 + sweepMask + innerGlow) + edgeGlow + arcEdge + targetOutline) * opacity;

    if (alpha < 0.005) discard;

    vec3 finalColor = color * (1.0 + innerGlow * 0.8 + edgeGlow * 1.2);
    gl_FragColor = vec4(finalColor, alpha);
  }
`;

type ScannerConeVisual = {
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>;
  controllableId: string;
};

const UNIT_BODY_VERTEX_SHADER = `
  attribute vec3 instanceBodyColor;
  attribute float instanceKind;
  attribute float instanceOpacity;

  varying vec2 vLocalUv;
  varying float vKind;
  varying float vOpacity;
  varying vec3 vColor;

  void main() {
    vLocalUv = uv * 2.0 - 1.0;
    vKind = instanceKind;
    vOpacity = instanceOpacity;
    vColor = instanceBodyColor;
    gl_Position = projectionMatrix * modelViewMatrix * instanceMatrix * vec4(position, 1.0);
  }
`;

const UNIT_BODY_FRAGMENT_SHADER = `
  uniform float time;

  varying vec2 vLocalUv;
  varying float vKind;
  varying float vOpacity;
  varying vec3 vColor;

  const float PI = 3.14159265359;

  float maskCircle(vec2 point, float radius) {
    return 1.0 - smoothstep(radius, radius + 0.035, length(point));
  }

  float maskEllipse(vec2 point, vec2 radii) {
    return 1.0 - smoothstep(1.0, 1.05, length(point / radii));
  }

  float maskRoundedBox(vec2 point, vec2 halfSize, float radius) {
    vec2 distance = abs(point) - halfSize + vec2(radius);
    float signedDistance = length(max(distance, 0.0)) + min(max(distance.x, distance.y), 0.0) - radius;
    return 1.0 - smoothstep(0.0, 0.04, signedDistance);
  }

  float maskRing(vec2 point, float innerRadius, float outerRadius) {
    float distance = length(point);
    float outer = 1.0 - smoothstep(outerRadius, outerRadius + 0.03, distance);
    float inner = smoothstep(innerRadius - 0.03, innerRadius + 0.03, distance);
    return outer * inner;
  }

  float maskDiamond(vec2 point, vec2 halfSize) {
    float distance = abs(point.x / halfSize.x) + abs(point.y / halfSize.y);
    return 1.0 - smoothstep(1.0, 1.08, distance);
  }

  float hash21(vec2 point) {
    point = fract(point * vec2(123.34, 456.21));
    point += dot(point, point + 45.32);
    return fract(point.x * point.y);
  }

  float stripeField(vec2 point, float frequency, float warp) {
    float band = sin(point.y * frequency + sin(point.x * warp) * 1.6 + point.x * 0.9);
    return 0.5 + 0.5 * band;
  }

  float pulse(float speed, float phase) {
    return 0.5 + 0.5 * sin(time * speed + phase);
  }

  float craterField(vec2 point) {
    vec2 shifted = point * 3.6;
    float craterA = 1.0 - smoothstep(0.18, 0.3, length(fract(shifted + vec2(0.12, 0.31)) - 0.5));
    float craterB = 1.0 - smoothstep(0.14, 0.26, length(fract(shifted * 0.82 + vec2(0.67, 0.18)) - 0.5));
    float craterC = 1.0 - smoothstep(0.16, 0.29, length(fract(shifted * 1.17 + vec2(0.41, 0.77)) - 0.5));
    return max(max(craterA, craterB), craterC);
  }

  vec3 applyPlanetLighting(vec2 point, vec3 baseColor, float surfaceMask, float atmosphereMask, float surfaceNoise) {
    vec2 lightDir = normalize(vec2(-0.7, 0.45));
    float normalZ = sqrt(max(0.0, 1.0 - dot(point, point)));
    vec3 normal = normalize(vec3(point * 0.92, normalZ));
    vec3 light = normalize(vec3(lightDir, 0.75));
    float diffuse = clamp(dot(normal, light), 0.0, 1.0);
    float fresnel = pow(1.0 - max(normal.z, 0.0), 2.4);
    vec3 lit = baseColor * (0.42 + diffuse * 0.9);
    lit += vec3(0.55, 0.76, 1.0) * atmosphereMask * (0.15 + fresnel * 0.5);
    lit += vec3(1.0) * surfaceNoise * surfaceMask * 0.07;
    return lit;
  }

  float maskShip(vec2 point) {
    return maskCircle(point, 0.7);
  }

  float maskShipDirection(vec2 point) {
    vec2 markerPoint = point - vec2(0.02, 0.0);
    float shaft = maskRoundedBox(markerPoint + vec2(0.12, 0.0), vec2(0.22, 0.055), 0.035);
    float head = maskDiamond(markerPoint - vec2(0.22, 0.0), vec2(0.18, 0.14));
    return max(shaft, head);
  }

  void main() {
    vec2 point = vLocalUv;
    vec2 animatedPoint = point;
    float baseMask = 0.0;
    float glowMask = 0.0;
    float kind = vKind;
    vec3 color = vColor;
    float emissiveBoost = 1.0;
    vec3 detailColor = vec3(0.0);

    if (kind < 0.5) {
      float prismPulse = pulse(2.2, point.x * 2.4 + point.y * 1.8);
      baseMask = maskDiamond(point, vec2(0.84 + prismPulse * 0.1, 0.84 + prismPulse * 0.1));
      glowMask = maskDiamond(point, vec2(1.0, 1.0)) * (0.08 + prismPulse * 0.08);
    } else if (kind < 1.5) {
      float distance = length(point);
      float angle = atan(point.y, point.x);
      float turbulence =
        sin(angle * 6.0 + distance * 11.0 - time * 2.8) * 0.5 +
        sin(angle * 11.0 - distance * 17.0 + time * 4.1) * 0.3 +
        cos(point.x * 12.0 - point.y * 9.0 + time * 3.2) * 0.2;
      float flare = 0.5 + 0.5 * turbulence;
      float coreMask = maskCircle(point, 0.44);
      float plasmaMask = maskCircle(point, 0.62 + (flare - 0.5) * 0.1);
      float coronaMask = 1.0 - smoothstep(0.68 + (flare - 0.5) * 0.18, 1.02, distance);
      float emberBand = smoothstep(0.52, 0.95, flare) * (1.0 - smoothstep(0.2, 0.72, distance));

      baseMask = max(plasmaMask, coreMask);
      glowMask = coronaMask * 0.58 + emberBand * 0.28;

      vec3 emberColor = vec3(1.0, 0.32, 0.06);
      vec3 flameColor = vec3(1.0, 0.62, 0.14);
      vec3 coreColor = vec3(1.0, 0.95, 0.74);
      color = mix(emberColor, flameColor, smoothstep(0.18, 0.72, 1.0 - distance));
      color = mix(color, coreColor, coreMask * 0.92 + emberBand * 0.24);
      emissiveBoost = 1.16 + flare * 0.24;
    } else if (kind < 4.5) {
      float distance = length(point);
      float surfaceMask = maskCircle(point, 0.7);
      float atmosphereMask = maskCircle(point, 0.82) - surfaceMask * 0.72;
      float rimMask = smoothstep(0.32, 0.92, distance) * surfaceMask;
      baseMask = surfaceMask;
      glowMask = maskCircle(point, 0.95) * 0.12 + atmosphereMask * 0.55;

      if (kind < 2.5) {
        animatedPoint.x += time * 0.045;
        float bands = stripeField(animatedPoint, 13.0, 6.5);
        float storms = 0.5 + 0.5 * sin(animatedPoint.x * 9.0 - animatedPoint.y * 4.0 + bands * 4.5 + time * 1.4);
        float surfaceNoise = bands * 0.7 + storms * 0.3;
        vec3 oceanColor = mix(color * 0.72, color * 1.16 + vec3(0.08, 0.14, 0.18), surfaceNoise);
        vec3 cloudColor = vec3(0.92, 0.98, 1.0);
        color = applyPlanetLighting(point, oceanColor, surfaceMask, atmosphereMask, surfaceNoise);
        detailColor += cloudColor * smoothstep(0.6, 0.88, surfaceNoise) * rimMask * 0.24;
      } else if (kind < 3.5) {
        float craterNoise = craterField(point + vec2(0.08, -0.03) + vec2(sin(time * 0.18), cos(time * 0.18)) * 0.015);
        float surfaceNoise = 0.35 + hash21(floor((point + 1.0) * 6.0 + vec2(time * 0.12))) * 0.65;
        vec3 moonBase = mix(color * 0.7, vec3(0.88, 0.9, 0.96), surfaceNoise * 0.36);
        color = applyPlanetLighting(point, moonBase, surfaceMask, atmosphereMask * 0.45, surfaceNoise * 0.4);
        detailColor -= vec3(0.1, 0.1, 0.12) * craterNoise * surfaceMask * 0.45;
      } else {
        float rockNoise = 0.5 + 0.5 * sin(point.x * 10.0 + point.y * 8.0 + sin(point.x * 4.0 + time * 0.8) + time * 0.55);
        vec3 rockColor = mix(color * 0.72, color * 1.18 + vec3(0.08, 0.06, 0.03), rockNoise);
        color = applyPlanetLighting(point, rockColor, surfaceMask, atmosphereMask * 0.25, rockNoise * 0.5);
        detailColor += vec3(1.0, 0.74, 0.45) * rimMask * 0.06;
      }
    } else if (kind < 6.5) {
      baseMask = maskRing(point, 0.42, 0.78);
      float swirl = sin(atan(point.y, point.x) * 5.0 - time * 2.2) * 0.5 + 0.5;
      glowMask = maskCircle(point, 1.05) * (0.12 + swirl * 0.18);
      detailColor += vec3(0.35, 0.9, 1.0) * baseMask * swirl * 0.3;
    } else if (kind < 9.5) {
      float hullMask = maskShip(point);
      float coreMask = maskCircle(point, 0.52);
      float directionMask = maskShipDirection(point) * coreMask;
      float innerShadow = smoothstep(0.12, 0.74, length(point)) * hullMask;
      float rimMask = hullMask - coreMask * 0.92;
      float enginePulse = pulse(6.5, point.x * 4.0);
      float forwardGlow = maskEllipse(point - vec2(0.18 + enginePulse * 0.04, 0.0), vec2(0.24, 0.18)) * (0.22 + enginePulse * 0.18);
      vec3 hullColor = mix(color * 0.68, color * 1.08 + vec3(0.08, 0.1, 0.14), smoothstep(-0.75, 0.45, point.x));

      baseMask = hullMask;
      glowMask = maskCircle(point, 0.96) * 0.12 + forwardGlow;
      color = hullColor * (0.82 + (1.0 - innerShadow) * 0.28);
      color *= 1.0 - directionMask * 0.92;
      detailColor += vec3(1.0) * rimMask * 0.16;
      detailColor += vec3(0.6, 0.9, 1.0) * forwardGlow * 0.42;
    } else if (kind < 10.5) {
      baseMask = maskEllipse(point, vec2(0.66, 0.2));
      float shotTrail = 0.5 + 0.5 * sin(time * 12.0 - point.x * 14.0);
      glowMask = maskEllipse(point, vec2(1.0, 0.34)) * (0.24 + shotTrail * 0.21);
      detailColor += vec3(1.0, 0.92, 0.55) * shotTrail * baseMask * 0.24;
    } else {
      baseMask = maskRoundedBox(point, vec2(0.82, 0.12), 0.08);
      float railPulse = 0.5 + 0.5 * sin(time * 9.0 - point.x * 10.0);
      glowMask = maskRoundedBox(point, vec2(1.0, 0.22), 0.1) * (0.2 + railPulse * 0.2);
      detailColor += vec3(1.0, 0.62, 0.32) * railPulse * baseMask * 0.22;
    }

    float alpha = clamp((baseMask + glowMask) * vOpacity, 0.0, 1.0);
    if (alpha < 0.01) {
      discard;
    }

    vec3 finalColor = (color + detailColor) * (baseMask + glowMask * 0.95) * emissiveBoost;
    gl_FragColor = vec4(finalColor, alpha);
  }
`;

const UNIT_KIND_CODE: Record<string, number> = {
  default: 0,
  sun: 1,
  planet: 2,
  moon: 3,
  meteoroid: 4,
  wormhole: 5,
  gate: 6,
  'classic-ship': 7,
  'modern-ship': 8,
  ship: 9,
  shot: 10,
  rail: 11,
};

export class WorldScene {
  private readonly container: HTMLElement;
  private readonly onSelection?: (selection: WorldSceneSelection) => void;
  private readonly onNavigationTargetRequested?: (selection: WorldSceneSelection) => void;
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene: THREE.Scene;
  private readonly camera: THREE.OrthographicCamera;
  private readonly grid: THREE.Group;
  private readonly root: THREE.Group;
  private bodyMesh: THREE.InstancedMesh<THREE.PlaneGeometry, UnitShaderMaterial>;
  private bodyColorAttribute: THREE.InstancedBufferAttribute;
  private bodyKindAttribute: THREE.InstancedBufferAttribute;
  private bodyOpacityAttribute: THREE.InstancedBufferAttribute;
  private readonly selectedRing: THREE.LineLoop;
  private readonly navigationMarker: THREE.Group;
  private readonly unitBodyMaterial: UnitShaderMaterial;
  private readonly resizeObserver: ResizeObserver;
  private readonly unitVisuals: Map<string, UnitVisual>;
  private readonly teamColors: Map<string, string>;
  private readonly debugUnits: DebugUnit[];
  private readonly tempBodyTransform: THREE.Object3D;
  private readonly tempColor: THREE.Color;
  private renderableUnits: NormalizedUnit[];
  private snapshot: GalaxySnapshotDto | null;
  private ownerOverlay: Record<string, OwnerOverlayState>;
  private selectedControllableId: string;
  private selectedUnitId: string;
  private selectedNavigationTarget: WorldSceneNavigationTarget;
  private readonly scannerCones: Map<string, ScannerConeVisual>;
  private animationFrame: number | null;
  private dragStartClient: { x: number; y: number } | null;
  private dragStartCamera: { x: number; y: number } | null;
  private dragPointerButton: number | null;
  private totalDragDistance: number;
  private isPointerCaptured: boolean;
  private isDisposed: boolean;

  constructor(container: HTMLElement, options: WorldSceneOptions = {}) {
    this.container = container;
    this.onSelection = options.onSelection;
    this.onNavigationTargetRequested = options.onNavigationTargetRequested;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setClearColor(0x050811, 1);

    this.scene = new THREE.Scene();
    this.camera = new THREE.OrthographicCamera(
      -DEFAULT_VIEW_HALF_WIDTH,
      DEFAULT_VIEW_HALF_WIDTH,
      DEFAULT_VIEW_HALF_HEIGHT,
      -DEFAULT_VIEW_HALF_HEIGHT,
      0.1,
      5000,
    );
    this.camera.position.set(0, 0, 100);
    this.camera.lookAt(0, 0, 0);

    this.grid = this.createGrid();
    this.root = new THREE.Group();
    this.unitBodyMaterial = createUnitBodyMaterial();
    const unitBodyMesh = createUnitBodyMesh(Math.max(DEBUG_SUN_COUNT, 1), this.unitBodyMaterial);
    this.bodyMesh = unitBodyMesh.mesh;
    this.bodyColorAttribute = unitBodyMesh.colorAttribute;
    this.bodyKindAttribute = unitBodyMesh.kindAttribute;
    this.bodyOpacityAttribute = unitBodyMesh.opacityAttribute;
    this.selectedRing = createSelectionRing();
    this.navigationMarker = createNavigationMarker();
    this.scannerCones = new Map();
    this.scene.add(this.grid);
    this.scene.add(this.bodyMesh);
    this.scene.add(this.root);
    this.scene.add(this.createGlowField());
    this.root.add(this.selectedRing);
    this.root.add(this.navigationMarker);

    this.snapshot = null;
    this.ownerOverlay = {};
    this.selectedControllableId = '';
    this.selectedUnitId = '';
    this.selectedNavigationTarget = null;
    this.unitVisuals = new Map<string, UnitVisual>();
    this.teamColors = new Map<string, string>();
    this.debugUnits = createDebugSuns();
    this.tempBodyTransform = new THREE.Object3D();
    this.tempColor = new THREE.Color();
    this.renderableUnits = [];
    this.animationFrame = null;
    this.dragStartClient = null;
    this.dragStartCamera = null;
    this.dragPointerButton = null;
    this.totalDragDistance = 0;
    this.isPointerCaptured = false;
    this.isDisposed = false;

    this.renderer.domElement.classList.add('world-canvas');
    this.container.appendChild(this.renderer.domElement);
    this.container.addEventListener('pointerdown', this.handlePointerDown);
    this.container.addEventListener('pointermove', this.handlePointerMove);
    this.container.addEventListener('pointerup', this.handlePointerUp);
    this.container.addEventListener('pointerleave', this.handlePointerLeave);
    this.container.addEventListener('contextmenu', this.handleContextMenu);
    this.container.addEventListener('wheel', this.handleWheel, { passive: false });

    this.resizeObserver = new ResizeObserver(() => {
      this.resize();
      this.requestRender();
    });
    this.resizeObserver.observe(this.container);

    this.resize();
    this.syncUnits();
    this.requestRender();
  }

  dispose() {
    this.isDisposed = true;
    if (this.animationFrame !== null) {
      window.cancelAnimationFrame(this.animationFrame);
      this.animationFrame = null;
    }

    this.resizeObserver.disconnect();
    this.container.removeEventListener('pointerdown', this.handlePointerDown);
    this.container.removeEventListener('pointermove', this.handlePointerMove);
    this.container.removeEventListener('pointerup', this.handlePointerUp);
    this.container.removeEventListener('pointerleave', this.handlePointerLeave);
    this.container.removeEventListener('contextmenu', this.handleContextMenu);
    this.container.removeEventListener('wheel', this.handleWheel);
    this.disposeBodyMesh();
    this.disposeVisuals();
    this.disposeScannerCones();
    this.unitBodyMaterial.dispose();
    this.renderer.dispose();
    this.container.innerHTML = '';
  }

  setSnapshot(snapshot: GalaxySnapshotDto | null, ownerOverlay: Record<string, unknown>, selectedControllableId: string, navigationTarget: WorldSceneNavigationTarget) {
    this.snapshot = snapshot;
    this.ownerOverlay = normalizeOwnerOverlay(ownerOverlay);
    this.selectedControllableId = selectedControllableId;
    this.selectedNavigationTarget = navigationTarget;
    this.teamColors.clear();

    if (snapshot) {
      for (const team of snapshot.teams) {
        this.teamColors.set(team.name, team.colorHex);
      }
    }

    this.syncUnits();
    this.requestRender();
  }

  focusOnSelection() {
    const targetUnitId = this.selectedControllableId || this.selectedUnitId;
    const unit = this.renderableUnits.find((candidate) => candidate.unitId === targetUnitId);
    if (!unit) {
      return;
    }

    this.camera.position.x = unit.x;
    this.camera.position.y = -unit.y;
    this.requestRender();
  }

  private readonly handlePointerDown = (event: PointerEvent) => {
    if (event.button === 2) {
      event.preventDefault();
    }

    this.dragStartClient = { x: event.clientX, y: event.clientY };
    this.dragStartCamera = { x: this.camera.position.x, y: this.camera.position.y };
    this.dragPointerButton = event.button;
    this.totalDragDistance = 0;
    this.isPointerCaptured = true;
    this.container.setPointerCapture(event.pointerId);
  };

  private readonly handlePointerMove = (event: PointerEvent) => {
    if (!this.dragStartClient || !this.dragStartCamera || !this.isPointerCaptured) {
      return;
    }

    const deltaX = event.clientX - this.dragStartClient.x;
    const deltaY = event.clientY - this.dragStartClient.y;
    this.totalDragDistance = Math.max(this.totalDragDistance, Math.hypot(deltaX, deltaY));

    if (this.dragPointerButton !== 0) {
      return;
    }

    this.camera.position.x = this.dragStartCamera.x - deltaX / this.camera.zoom;
    this.camera.position.y = this.dragStartCamera.y + deltaY / this.camera.zoom;
    this.requestRender();
  };

  private readonly handlePointerUp = (event: PointerEvent) => {
    if (!this.dragStartClient && !this.isPointerCaptured) {
      return;
    }

    if (this.isPointerCaptured) {
      this.container.releasePointerCapture(event.pointerId);
    }

    const shouldSelect = this.totalDragDistance < 6;
    const worldPosition = this.clientToWorld(event.clientX, event.clientY);
    const pointerButton = this.dragPointerButton;

    this.dragStartClient = null;
    this.dragStartCamera = null;
    this.dragPointerButton = null;
    this.totalDragDistance = 0;
    this.isPointerCaptured = false;

    if (!shouldSelect) {
      return;
    }

    if (pointerButton === 2) {
      const unit = this.findNearestUnit(worldPosition.x, worldPosition.y);
      this.onNavigationTargetRequested?.({
        worldX: worldPosition.x,
        worldY: worldPosition.y,
        unitId: unit?.unitId,
        kind: unit?.kind,
        teamName: unit?.teamName,
      });
      return;
    }

    const unit = this.findNearestUnit(worldPosition.x, worldPosition.y);
    this.selectedUnitId = unit?.unitId ?? '';
    this.updateSelectionRing();
    this.requestRender();
    this.onSelection?.({
      worldX: worldPosition.x,
      worldY: worldPosition.y,
      unitId: unit?.unitId,
      kind: unit?.kind,
      teamName: unit?.teamName,
    });
  };

  private readonly handlePointerLeave = (event: PointerEvent) => {
    if (!this.dragStartClient && !this.isPointerCaptured) {
      return;
    }

    if (this.isPointerCaptured) {
      this.container.releasePointerCapture(event.pointerId);
    }

    this.dragStartClient = null;
    this.dragStartCamera = null;
    this.dragPointerButton = null;
    this.totalDragDistance = 0;
    this.isPointerCaptured = false;
  };

  private readonly handleContextMenu = (event: MouseEvent) => {
    event.preventDefault();
  };

  private readonly handleWheel = (event: WheelEvent) => {
    event.preventDefault();
    const worldBeforeZoom = this.clientToWorld(event.clientX, event.clientY);
    const zoomFactor = Math.exp(event.deltaY * 0.0012);
    this.camera.zoom = THREE.MathUtils.clamp(this.camera.zoom * zoomFactor, 0.12, 8);
    this.camera.updateProjectionMatrix();

    const worldAfterZoom = this.clientToWorld(event.clientX, event.clientY);
    this.camera.position.x += worldBeforeZoom.x - worldAfterZoom.x;
    this.camera.position.y += worldBeforeZoom.y - worldAfterZoom.y;
    this.requestRender();
  };

  private resize() {
    const width = Math.max(this.container.clientWidth, 1);
    const height = Math.max(this.container.clientHeight, 1);
    const aspect = width / height;
    const halfWidth = DEFAULT_VIEW_HALF_HEIGHT * aspect;

    this.camera.left = -halfWidth;
    this.camera.right = halfWidth;
    this.camera.top = DEFAULT_VIEW_HALF_HEIGHT;
    this.camera.bottom = -DEFAULT_VIEW_HALF_HEIGHT;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(width, height, false);
  }

  private requestRender() {
    if (this.animationFrame !== null) {
      return;
    }

    this.animationFrame = window.requestAnimationFrame(this.renderFrame);
  }

  private readonly renderFrame = (timestamp: number) => {
    if (this.isDisposed) {
      this.animationFrame = null;
      return;
    }

    const time = timestamp * 0.001;
    this.unitBodyMaterial.uniforms.time.value = time;

    for (const [, cone] of this.scannerCones) {
      cone.mesh.material.uniforms.time.value = time;
    }

    this.renderer.render(this.scene, this.camera);
    this.animationFrame = window.requestAnimationFrame(this.renderFrame);
  };

  private syncUnits() {
    const units = this.buildRenderableUnits();
    this.renderableUnits = units;
    this.ensureBodyMeshCapacity(units.length);

    const liveDirectionalIds = new Set(units.filter((unit) => isDirectionalKind(unit.renderKind)).map((unit) => unit.unitId));

    for (const [unitId, visual] of this.unitVisuals.entries()) {
      if (liveDirectionalIds.has(unitId)) {
        continue;
      }

      if (visual.heading) {
        this.root.remove(visual.heading);
      }
      this.disposeVisual(visual);
      this.unitVisuals.delete(unitId);
    }

    for (let index = 0; index < units.length; index++) {
      const unit = units[index];
      this.updateBodyInstance(index, unit);

      if (!isDirectionalKind(unit.renderKind)) {
        continue;
      }

      let visual = this.unitVisuals.get(unit.unitId);
      if (!visual) {
        visual = this.createVisual();
        this.unitVisuals.set(unit.unitId, visual);
        if (visual.heading) {
          this.root.add(visual.heading);
        }
      }

      this.updateVisual(visual, unit, index);
    }

    this.bodyMesh.count = units.length;
    this.bodyMesh.instanceMatrix.needsUpdate = true;
    this.bodyColorAttribute.needsUpdate = true;
    this.bodyKindAttribute.needsUpdate = true;
    this.bodyOpacityAttribute.needsUpdate = true;
    this.updateSelectionRing();
    this.updateNavigationMarker();
    this.updateScannerCones(units);
  }

  private buildRenderableUnits(): NormalizedUnit[] {
    if (!this.snapshot) {
      return [...this.debugUnits];
    }

    const deadControllableIds = new Set(
      this.snapshot.controllables
        .filter((controllable) => !getControllableAliveState(controllable, this.ownerOverlay[controllable.controllableId]))
        .map((controllable) => controllable.controllableId),
    );
    const units = this.snapshot.units.filter((unit) => !deadControllableIds.has(unit.unitId));

    for (const controllable of this.snapshot.controllables) {
      if (deadControllableIds.has(controllable.controllableId)) {
        continue;
      }

      const overlay = this.ownerOverlay[controllable.controllableId];
      if (overlay) {
        units.push({
          unitId: controllable.controllableId,
          clusterId: numberValue(overlay.clusterId, 0),
          kind: stringValue(overlay.kind, 'controllable_marker'),
          x: numberValue((overlay.position as OwnerOverlayState | undefined)?.x, 0),
          y: numberValue((overlay.position as OwnerOverlayState | undefined)?.y, 0),
          angle: numberValue((overlay.position as OwnerOverlayState | undefined)?.angle, 0),
          radius: numberValue(overlay.radius, getFallbackRenderRadius(normalizeKind(stringValue(overlay.kind, 'controllable_marker')))),
          teamName: controllable.teamName,
        });
        continue;
      }

      if (units.some((unit) => unit.unitId === controllable.controllableId)) {
        continue;
      }

      units.push({
        unitId: controllable.controllableId,
        clusterId: 0,
        kind: 'controllable_marker',
        x: 0,
        y: 0,
        angle: 0,
        radius: getFallbackRenderRadius('ship'),
        teamName: controllable.teamName,
      });
    }

    return [
      ...units.map((unit) => ({
        ...unit,
        renderKind: normalizeKind(unit.kind),
        renderRadius: getRenderRadius(unit.radius, normalizeKind(unit.kind)),
      })),
      ...this.debugUnits,
    ];
  }

  private createVisual(): UnitVisual {
    return {
      heading: new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(getHeadingPoints('shot')),
        new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.8 }),
      ),
      instanceIndex: -1,
    };
  }

  private updateVisual(visual: UnitVisual, unit: NormalizedUnit, instanceIndex: number) {
    const scale = getRenderScale(unit);
    const bodyColor = this.tempColor.set(getColorForUnit(unit, this.teamColors));
    visual.instanceIndex = instanceIndex;

    if (visual.heading) {
      const headingMaterial = visual.heading.material as THREE.LineBasicMaterial;
      visual.heading.geometry.setFromPoints(getHeadingPoints(unit.renderKind));
      visual.heading.position.set(unit.x, -unit.y, 0);
      visual.heading.rotation.z = toSceneRotation(unit.angle);
      headingMaterial.color.copy(bodyColor);
      const headingScale = isShipKind(unit.renderKind) ? scale * 0.85 : scale * 1.8;
      visual.heading.scale.set(headingScale, headingScale, 1);
    }
  }

  private updateBodyInstance(index: number, unit: NormalizedUnit) {
    const scale = getRenderScale(unit);
    const bodyColor = this.tempColor.set(getColorForUnit(unit, this.teamColors));

    this.tempBodyTransform.position.set(unit.x, -unit.y, 0);
    this.tempBodyTransform.rotation.set(0, 0, toSceneRotation(unit.angle));
    this.tempBodyTransform.scale.set(scale, scale, 1);
    this.tempBodyTransform.updateMatrix();

    this.bodyMesh.setMatrixAt(index, this.tempBodyTransform.matrix);
    this.bodyColorAttribute.setXYZ(index, bodyColor.r, bodyColor.g, bodyColor.b);
    this.bodyKindAttribute.setX(index, getShaderKindCode(unit.renderKind));
    this.bodyOpacityAttribute.setX(index, getOpacityForKind(unit.renderKind));
  }

  private updateSelectionRing() {
    const selectedUnit = this.renderableUnits.find((unit) => unit.unitId === this.selectedControllableId || unit.unitId === this.selectedUnitId);
    if (!selectedUnit) {
      this.selectedRing.visible = false;
      return;
    }

    const selectionRadius = Math.max(selectedUnit.renderRadius * 1.18, 4);
    this.selectedRing.visible = true;
    this.selectedRing.position.set(selectedUnit.x, -selectedUnit.y, 0);
    this.selectedRing.scale.set(selectionRadius / 1.25, selectionRadius / 1.25, 1);
  }

  private updateNavigationMarker() {
    if (!this.selectedNavigationTarget) {
      this.navigationMarker.visible = false;
      return;
    }

    const markerScale = Math.max(10, this.getWorldUnitsPerPixel() * 22);
    this.navigationMarker.visible = true;
    this.navigationMarker.position.set(this.selectedNavigationTarget.x, -this.selectedNavigationTarget.y, 0);
    this.navigationMarker.scale.set(markerScale, markerScale, 1);
  }

  private updateScannerCones(units: NormalizedUnit[]) {
    // Collect controllable IDs that have active scanners in the overlay
    const activeScanners = new Map<string, {
      unit: NormalizedUnit;
      scannerState: OwnerOverlayState;
      teamColor: string;
    }>();

    for (const [controllableId, overlay] of Object.entries(this.ownerOverlay)) {
      const scannerState = overlay.scanner as OwnerOverlayState | undefined;
      if (!scannerState || scannerState.active !== true) continue;

      const unit = units.find((u) => u.unitId === controllableId);
      if (!unit) continue;

      const teamColor = this.teamColors.get(unit.teamName ?? '') ?? '#7cb3dd';
      activeScanners.set(controllableId, { unit, scannerState, teamColor });
    }

    // Remove cones for controllables no longer active
    for (const [cId, cone] of this.scannerCones) {
      if (!activeScanners.has(cId)) {
        this.root.remove(cone.mesh);
        cone.mesh.geometry.dispose();
        cone.mesh.material.dispose();
        this.scannerCones.delete(cId);
      }
    }

    // Create or update cones
    for (const [cId, { unit, scannerState, teamColor }] of activeScanners) {
      const currentWidth = numberValue(scannerState.currentWidth, 90);
      const currentLength = numberValue(scannerState.currentLength, 200);
      const currentAngle = numberValue(scannerState.currentAngle, 0);
      const targetWidth = numberValue(scannerState.targetWidth, currentWidth);
      const targetLength = numberValue(scannerState.targetLength, currentLength);

      // Half-angle in radians
      const halfWidthRad = (currentWidth / 2) * (Math.PI / 180);
      const targetHalfWidthRad = (targetWidth / 2) * (Math.PI / 180);
      // Normalize target length relative to current length for shader
      const normalizedTargetLength = currentLength > 0 ? targetLength / currentLength : 1;

      let cone = this.scannerCones.get(cId);
      if (!cone) {
        const geo = new THREE.PlaneGeometry(2, 2);
        const mat = new THREE.ShaderMaterial({
          uniforms: {
            time: { value: 0 },
            halfWidthRad: { value: halfWidthRad },
            coneLength: { value: currentLength },
            targetHalfWidthRad: { value: targetHalfWidthRad },
            targetLength: { value: normalizedTargetLength },
            color: { value: new THREE.Color(teamColor) },
            opacity: { value: 0.7 },
          },
          vertexShader: SCANNER_CONE_VERTEX_SHADER,
          fragmentShader: SCANNER_CONE_FRAGMENT_SHADER,
          blending: THREE.AdditiveBlending,
          transparent: true,
          depthWrite: false,
          side: THREE.DoubleSide,
        });
        const mesh = new THREE.Mesh(geo, mat);
        mesh.frustumCulled = false;
        cone = { mesh, controllableId: cId };
        this.scannerCones.set(cId, cone);
        this.root.add(mesh);
      }

      // Update uniforms
      const uniforms = cone.mesh.material.uniforms;
      uniforms.time.value = this.unitBodyMaterial.uniforms.time.value;
      uniforms.halfWidthRad.value = halfWidthRad;
      uniforms.coneLength.value = currentLength;
      uniforms.targetHalfWidthRad.value = targetHalfWidthRad;
      uniforms.targetLength.value = normalizedTargetLength;
      (uniforms.color.value as THREE.Color).set(teamColor);

      // Position: at the controllable, rotated so +X aligns with scanner angle
      const scale = currentLength;
      cone.mesh.position.set(unit.x, -unit.y, 0);
      cone.mesh.rotation.set(0, 0, -currentAngle * (Math.PI / 180));
      cone.mesh.scale.set(scale, scale, 1);
    }
  }

  private findNearestUnit(worldX: number, worldY: number): NormalizedUnit | null {
    const units = this.renderableUnits;
    if (units.length === 0) {
      return null;
    }

    let nearest: NormalizedUnit | null = null;
    let nearestScore = Number.POSITIVE_INFINITY;

    for (const unit of units) {
      const dx = unit.x - worldX;
      const dy = -unit.y - worldY;
      const distance = Math.hypot(dx, dy);
      const pickRadius = this.getUnitPickRadius(unit);
      if (distance > pickRadius) {
        continue;
      }

      const pickScore = distance / pickRadius;
      if (pickScore >= nearestScore) {
        continue;
      }

      nearest = unit;
      nearestScore = pickScore;
    }

    return nearest;
  }

  private getUnitPickRadius(unit: NormalizedUnit) {
    const minimumPickRadius = (MIN_PICK_RADIUS_PX + PICK_RADIUS_PADDING_PX) * this.getWorldUnitsPerPixel();
    return Math.max(unit.renderRadius, minimumPickRadius);
  }

  private getWorldUnitsPerPixel() {
    const width = Math.max(this.container.clientWidth, 1);
    const height = Math.max(this.container.clientHeight, 1);
    const visibleWorldWidth = (this.camera.right - this.camera.left) / this.camera.zoom;
    const visibleWorldHeight = (this.camera.top - this.camera.bottom) / this.camera.zoom;
    return Math.max(visibleWorldWidth / width, visibleWorldHeight / height);
  }

  private clientToWorld(clientX: number, clientY: number) {
    const rect = this.container.getBoundingClientRect();
    const ndc = new THREE.Vector3(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1,
      0,
    );
    ndc.unproject(this.camera);
    return { x: ndc.x, y: ndc.y };
  }

  private createGrid() {
    const grid = new THREE.Group();
    const minorMaterial = new THREE.LineBasicMaterial({ color: 0x153042, transparent: true, opacity: 0.55 });
    const majorMaterial = new THREE.LineBasicMaterial({ color: 0x2b6d80, transparent: true, opacity: 0.42 });

    for (let index = -20; index <= 20; index++) {
      const coordinate = index * 120;
      const material = index % 5 === 0 ? majorMaterial : minorMaterial;

      grid.add(new THREE.Line(
        new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(-2400, coordinate, -20), new THREE.Vector3(2400, coordinate, -20)]),
        material,
      ));
      grid.add(new THREE.Line(
        new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(coordinate, -2400, -20), new THREE.Vector3(coordinate, 2400, -20)]),
        material,
      ));
    }

    return grid;
  }

  private createGlowField() {
    const stars = new THREE.Group();
    const geometry = new THREE.CircleGeometry(1, 10);
    const material = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.12 });

    for (let index = 0; index < 140; index++) {
      const star = new THREE.Mesh(geometry, material.clone());
      star.position.set((Math.random() - 0.5) * 4200, (Math.random() - 0.5) * 2400, -100);
      const scale = 1 + Math.random() * 2.2;
      star.scale.set(scale, scale, 1);
      (star.material as THREE.MeshBasicMaterial).opacity = 0.05 + Math.random() * 0.18;
      stars.add(star);
    }

    return stars;
  }

  private disposeVisual(visual: UnitVisual) {
    visual.heading?.geometry.dispose();
    (visual.heading?.material as THREE.Material | undefined)?.dispose();
  }

  private disposeVisuals() {
    for (const visual of this.unitVisuals.values()) {
      this.disposeVisual(visual);
    }

    this.unitVisuals.clear();
    this.selectedRing.geometry.dispose();
    (this.selectedRing.material as THREE.Material).dispose();
    for (const child of this.navigationMarker.children) {
      if (child instanceof THREE.Line || child instanceof THREE.LineLoop || child instanceof THREE.LineSegments) {
        child.geometry.dispose();
        (child.material as THREE.Material).dispose();
      }
    }
  }

  private disposeBodyMesh() {
    this.bodyMesh.geometry.dispose();
  }

  private disposeScannerCones() {
    for (const [, cone] of this.scannerCones) {
      this.root.remove(cone.mesh);
      cone.mesh.geometry.dispose();
      cone.mesh.material.dispose();
    }
    this.scannerCones.clear();
  }

  private ensureBodyMeshCapacity(requiredCount: number) {
    if (requiredCount <= this.bodyColorAttribute.count) {
      return;
    }

    const nextCapacity = Math.max(requiredCount, Math.ceil(this.bodyColorAttribute.count * 1.5), 1);
    this.scene.remove(this.bodyMesh);
    this.disposeBodyMesh();

    const unitBodyMesh = createUnitBodyMesh(nextCapacity, this.unitBodyMaterial);
    this.bodyMesh = unitBodyMesh.mesh;
    this.bodyColorAttribute = unitBodyMesh.colorAttribute;
    this.bodyKindAttribute = unitBodyMesh.kindAttribute;
    this.bodyOpacityAttribute = unitBodyMesh.opacityAttribute;
    this.scene.add(this.bodyMesh);
  }
}

function createUnitBodyGeometry(instanceCapacity: number) {
  const geometry = new THREE.PlaneGeometry(2, 2);
  const colorAttribute = new THREE.InstancedBufferAttribute(new Float32Array(instanceCapacity * 3).fill(1), 3);
  const kindAttribute = new THREE.InstancedBufferAttribute(new Float32Array(instanceCapacity), 1);
  const opacityAttribute = new THREE.InstancedBufferAttribute(new Float32Array(instanceCapacity).fill(1), 1);

  colorAttribute.setUsage(THREE.DynamicDrawUsage);
  kindAttribute.setUsage(THREE.DynamicDrawUsage);
  opacityAttribute.setUsage(THREE.DynamicDrawUsage);

  geometry.setAttribute('instanceBodyColor', colorAttribute);
  geometry.setAttribute('instanceKind', kindAttribute);
  geometry.setAttribute('instanceOpacity', opacityAttribute);

  return geometry;
}

function createUnitBodyMesh(instanceCapacity: number, material: UnitShaderMaterial): UnitBodyMesh {
  const geometry = createUnitBodyGeometry(instanceCapacity);
  const mesh = new THREE.InstancedMesh(geometry, material, instanceCapacity);

  mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
  mesh.frustumCulled = false;

  return {
    mesh,
    colorAttribute: geometry.getAttribute('instanceBodyColor') as THREE.InstancedBufferAttribute,
    kindAttribute: geometry.getAttribute('instanceKind') as THREE.InstancedBufferAttribute,
    opacityAttribute: geometry.getAttribute('instanceOpacity') as THREE.InstancedBufferAttribute,
  };
}

function createUnitBodyMaterial() {
  return new THREE.ShaderMaterial({
    uniforms: {
      time: { value: 0 },
    },
    vertexShader: UNIT_BODY_VERTEX_SHADER,
    fragmentShader: UNIT_BODY_FRAGMENT_SHADER,
    blending: THREE.AdditiveBlending,
    transparent: true,
    depthWrite: false,
  }) as UnitShaderMaterial;
}

function createDebugSuns(): DebugUnit[] {
  const debugUnits: DebugUnit[] = [];

  for (let index = 0; index < DEBUG_SUN_COUNT; index++) {
    const radius = 22;
    debugUnits.push({
      unitId: `debug-sun-${index}`,
      clusterId: -1,
      kind: 'sun',
      x: randomInRange(-DEBUG_SUN_BOUNDS_X, DEBUG_SUN_BOUNDS_X),
      y: randomInRange(-DEBUG_SUN_BOUNDS_Y, DEBUG_SUN_BOUNDS_Y),
      angle: randomInRange(0, 360),
      radius,
      renderKind: 'sun',
      renderRadius: radius,
    });
  }

  return debugUnits;
}

function createRingPoints(segmentCount: number, radius: number) {
  const points: THREE.Vector3[] = [];
  for (let index = 0; index < segmentCount; index++) {
    const angle = (index / segmentCount) * Math.PI * 2;
    points.push(new THREE.Vector3(Math.cos(angle) * radius, Math.sin(angle) * radius, 0));
  }
  return points;
}

function createSelectionRing() {
  const ring = new THREE.LineLoop(
    new THREE.BufferGeometry().setFromPoints(createRingPoints(28, 1.25)),
    new THREE.LineBasicMaterial({ color: 0x9ff8ff, transparent: true, opacity: 0.95 }),
  );

  ring.visible = false;
  return ring;
}

function createNavigationMarker() {
  const marker = new THREE.Group();
  const ringMaterial = new THREE.LineBasicMaterial({ color: 0xffc857, transparent: true, opacity: 0.92 });
  const crossMaterial = new THREE.LineBasicMaterial({ color: 0xfff3c4, transparent: true, opacity: 0.88 });

  marker.add(new THREE.LineLoop(
    new THREE.BufferGeometry().setFromPoints(createRingPoints(20, 1)),
    ringMaterial,
  ));
  marker.add(new THREE.LineSegments(
    new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(-1.45, 0, 0),
      new THREE.Vector3(-0.5, 0, 0),
      new THREE.Vector3(0.5, 0, 0),
      new THREE.Vector3(1.45, 0, 0),
      new THREE.Vector3(0, -1.45, 0),
      new THREE.Vector3(0, -0.5, 0),
      new THREE.Vector3(0, 0.5, 0),
      new THREE.Vector3(0, 1.45, 0),
    ]),
    crossMaterial,
  ));

  marker.visible = false;
  return marker;
}

function createShipArrowPoints() {
  return [
    new THREE.Vector3(-0.42, 0, 0),
    new THREE.Vector3(0.34, 0, 0),
    new THREE.Vector3(0.08, 0.2, 0),
    new THREE.Vector3(0.34, 0, 0),
    new THREE.Vector3(0.08, -0.2, 0),
  ];
}

function getHeadingPoints(kind: string) {
  if (isShipKind(kind)) {
    return createShipArrowPoints();
  }

  return [new THREE.Vector3(0, 0, 0), new THREE.Vector3(1, 0, 0)];
}

function getShaderKindCode(kind: string) {
  return UNIT_KIND_CODE[kind] ?? UNIT_KIND_CODE.default;
}

function normalizeKind(kind: string) {
  const lower = kind.toLowerCase();
  if (lower.includes('classicshipplayerunit')) {
    return 'classic-ship';
  }
  if (lower.includes('modernshipplayerunit')) {
    return 'modern-ship';
  }
  if (lower.includes('ship')) {
    return 'ship';
  }
  if (lower.includes('planet')) {
    return 'planet';
  }
  if (lower.includes('moon')) {
    return 'moon';
  }
  if (lower.includes('meteoroid')) {
    return 'meteoroid';
  }
  if (lower.includes('wormhole')) {
    return 'wormhole';
  }
  if (lower.includes('gate')) {
    return 'gate';
  }
  if (lower.includes('shot')) {
    return 'shot';
  }
  if (lower.includes('rail')) {
    return 'rail';
  }
  return lower;
}

function getSizeForKind(kind: string) {
  switch (kind) {
    case 'sun':
      return 22;
    case 'planet':
      return 14;
    case 'moon':
      return 9;
    case 'wormhole':
    case 'gate':
      return 12;
    case 'classic-ship':
      return 9.5;
    case 'modern-ship':
    case 'ship':
      return 8.5;
    case 'shot':
    case 'rail':
      return 2.2;
    default:
      return 6;
  }
}

function getBodyExtent(kind: string) {
  switch (kind) {
    case 'wormhole':
    case 'gate':
      return 0.78;
    case 'shot':
      return 0.66;
    case 'rail':
      return 0.82;
    default:
      return 0.7;
  }
}

function getFallbackRenderRadius(kind: string) {
  return getSizeForKind(kind) * getBodyExtent(kind);
}

function getRenderRadius(radius: number | null | undefined, kind: string) {
  return typeof radius === 'number' && Number.isFinite(radius) && radius > 0
    ? radius
    : getFallbackRenderRadius(kind);
}

function getRenderScale(unit: NormalizedUnit) {
  return unit.renderRadius / getBodyExtent(unit.renderKind);
}

function getOpacityForKind(kind: string) {
  if (kind === 'wormhole' || kind === 'gate') {
    return 0.82;
  }
  if (kind === 'shot' || kind === 'rail') {
    return 0.9;
  }
  return 1;
}

function toSceneRotation(angleDegrees: number) {
  return -THREE.MathUtils.degToRad(angleDegrees);
}

function isDirectionalKind(kind: string) {
  return kind === 'shot' || kind === 'rail';
}

function isShipKind(kind: string) {
  return kind === 'classic-ship' || kind === 'modern-ship' || kind === 'ship';
}

function getColorForUnit(unit: NormalizedUnit, teamColors: Map<string, string>) {
  if (unit.teamName && teamColors.has(unit.teamName)) {
    return teamColors.get(unit.teamName)!;
  }

  switch (unit.renderKind) {
    case 'sun':
      return '#ffb347';
    case 'planet':
      return '#7dd3fc';
    case 'moon':
      return '#c9d8ff';
    case 'wormhole':
      return '#7aebff';
    case 'gate':
      return '#90f1b8';
    case 'shot':
      return '#ffd369';
    case 'rail':
      return '#ff8a5b';
    default:
      return '#d7ecff';
  }
}

export function formatTeamAccent(teamName: string | undefined, teams: TeamSnapshotDto[]) {
  if (!teamName) {
    return '#d7ecff';
  }

  return teams.find((team) => team.name === teamName)?.colorHex ?? '#d7ecff';
}

function normalizeOwnerOverlay(ownerOverlay: Record<string, unknown>) {
  const normalized: Record<string, OwnerOverlayState> = {};

  for (const [controllableId, value] of Object.entries(ownerOverlay)) {
    if (value && typeof value === 'object') {
      normalized[controllableId] = value as OwnerOverlayState;
    }
  }

  return normalized;
}

function getControllableAliveState(
  publicControllable: PublicControllableSnapshotDto,
  overlayState: unknown,
) {
  if (overlayState && typeof overlayState === 'object' && Object.prototype.hasOwnProperty.call(overlayState, 'alive')) {
    return typeof (overlayState as Record<string, unknown>).alive === 'boolean'
      ? ((overlayState as Record<string, unknown>).alive as boolean)
      : publicControllable.alive;
  }

  return publicControllable.alive;
}

function numberValue(value: unknown, fallback: number) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

export function readNavigationTarget(ownerOverlay: Record<string, unknown>, controllableId: string) {
  const overlay = ownerOverlay[controllableId];
  if (!overlay || typeof overlay !== 'object') {
    return null;
  }

  const navigation = (overlay as Record<string, unknown>).navigation;
  if (!navigation || typeof navigation !== 'object') {
    return null;
  }

  const navigationState = navigation as Record<string, unknown>;
  if (navigationState.active !== true) {
    return null;
  }

  return {
    x: numberValue(navigationState.targetX, 0),
    y: numberValue(navigationState.targetY, 0),
  };
}

function stringValue(value: unknown, fallback: string) {
  return typeof value === 'string' && value.length > 0 ? value : fallback;
}

function randomInRange(min: number, max: number) {
  return min + Math.random() * (max - min);
}