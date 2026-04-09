import * as THREE from 'three';
import type { GalaxySnapshotDto, PublicControllableSnapshotDto, TeamSnapshotDto } from '../types/generated';
import { DEFAULT_VIEW_HALF_HEIGHT, DEBUG_SUN_COUNT, MIN_PICK_RADIUS_PX, PICK_RADIUS_PADDING_PX } from './constants';
import { type NormalizedUnit, type UnitShaderMaterial, type UnitBodyMesh, createUnitBodyMaterial, createUnitBodyMesh, createDebugSuns } from './unitBody';
import { type UnitVisual, normalizeKind, getRenderRadius, getFallbackRenderRadius, getRenderScale, getColorForUnit, getShaderKindCode, getOpacityForUnit, toSceneRotation, isDirectionalKind, isShipKind, getHeadingPoints, shouldPersistTraceForUnit, isUnseenDynamicUnit } from './unitVisuals';
import { type ScannerConeVisual, createScannerConeMesh } from './scannerCone';
import { createSelectionRing } from './selectionRing';
import { createNavigationMarker } from './navigationMarker';
import { createNavigationPointer } from './navigationPointer';
import { createGrid, createGlowField } from './grid';

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

type WorldSceneNavigationPointer = {
  startX: number;
  startY: number;
  endX: number;
  endY: number;
  vectorX: number;
  vectorY: number;
} | null;

type WorldSceneOptions = {
  onSelection?: (selection: WorldSceneSelection) => void;
  onNavigationTargetRequested?: (selection: WorldSceneSelection) => void;
  onVisibleUnitsChanged?: (unitIds: string[]) => void;
  onFocusSelectionChanged?: (isActive: boolean) => void;
};

type OwnerOverlayState = Record<string, unknown>;

type BodyMeshResources = UnitBodyMesh;

type RawRenderableUnit = {
  unitId: string;
  clusterId: number;
  kind: string;
  isStatic: boolean;
  isSeen: boolean;
  lastSeenTick: number;
  x: number;
  y: number;
  angle: number;
  radius: number;
  teamName?: string;
};

export class WorldScene {
  private readonly container: HTMLElement;
  private readonly onSelection?: (selection: WorldSceneSelection) => void;
  private readonly onNavigationTargetRequested?: (selection: WorldSceneSelection) => void;
  private readonly onVisibleUnitsChanged?: (unitIds: string[]) => void;
  private readonly onFocusSelectionChanged?: (isActive: boolean) => void;
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene: THREE.Scene;
  private readonly camera: THREE.OrthographicCamera;
  private readonly grid: THREE.Group;
  private readonly root: THREE.Group;
  private staticBodies: BodyMeshResources;
  private dynamicBodies: BodyMeshResources;
  private readonly selectedRing: THREE.LineLoop;
  private readonly navigationMarker: THREE.Group;
  private readonly navigationPointer: THREE.LineSegments;
  private readonly unitBodyMaterial: UnitShaderMaterial;
  private readonly resizeObserver: ResizeObserver;
  private readonly unitVisuals: Map<string, UnitVisual>;
  private readonly teamColors: Map<string, string>;
  private readonly debugUnits: NormalizedUnit[];
  private readonly lastSeenTraces: Map<string, NormalizedUnit>;
  private readonly normalizedUnitsByKey: Map<string, { signature: string; unit: NormalizedUnit }>;
  private readonly tempBodyTransform: THREE.Object3D;
  private readonly tempColor: THREE.Color;
  private staticRenderableUnits: NormalizedUnit[];
  private dynamicRenderableUnits: NormalizedUnit[];
  private renderableUnits: NormalizedUnit[];
  private readonly renderableUnitsById: Map<string, NormalizedUnit>;
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
  private isFollowingSelection: boolean;
  private isDisposed: boolean;
  private lastReportedVisibleUnitIds: string[];

  constructor(container: HTMLElement, options: WorldSceneOptions = {}) {
    this.container = container;
    this.onSelection = options.onSelection;
    this.onNavigationTargetRequested = options.onNavigationTargetRequested;
    this.onVisibleUnitsChanged = options.onVisibleUnitsChanged;
    this.onFocusSelectionChanged = options.onFocusSelectionChanged;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setClearColor(0x050811, 1);

    this.scene = new THREE.Scene();
    this.camera = new THREE.OrthographicCamera(
      -DEFAULT_VIEW_HALF_HEIGHT,
      DEFAULT_VIEW_HALF_HEIGHT,
      DEFAULT_VIEW_HALF_HEIGHT,
      -DEFAULT_VIEW_HALF_HEIGHT,
      0.1,
      5000,
    );
    this.camera.position.set(0, 0, 100);
    this.camera.lookAt(0, 0, 0);

    this.grid = createGrid();
    this.root = new THREE.Group();
    this.unitBodyMaterial = createUnitBodyMaterial();
    this.staticBodies = createUnitBodyMesh(Math.max(DEBUG_SUN_COUNT, 1), this.unitBodyMaterial);
    this.dynamicBodies = createUnitBodyMesh(1, this.unitBodyMaterial);
    this.selectedRing = createSelectionRing();
    this.navigationMarker = createNavigationMarker();
    this.navigationPointer = createNavigationPointer();
    this.scannerCones = new Map();
    this.scene.add(this.grid);
    this.scene.add(this.staticBodies.mesh);
    this.scene.add(this.dynamicBodies.mesh);
    this.scene.add(this.root);
    this.scene.add(createGlowField());
    this.root.add(this.selectedRing);
    this.root.add(this.navigationMarker);
    this.root.add(this.navigationPointer);

    this.snapshot = null;
    this.ownerOverlay = {};
    this.selectedControllableId = '';
    this.selectedUnitId = '';
    this.selectedNavigationTarget = null;
    this.unitVisuals = new Map<string, UnitVisual>();
    this.teamColors = new Map<string, string>();
    this.debugUnits = createDebugSuns();
    this.lastSeenTraces = new Map<string, NormalizedUnit>();
    this.normalizedUnitsByKey = new Map<string, { signature: string; unit: NormalizedUnit }>();
    this.tempBodyTransform = new THREE.Object3D();
    this.tempColor = new THREE.Color();
    this.staticRenderableUnits = [];
    this.dynamicRenderableUnits = [];
    this.renderableUnits = [];
    this.renderableUnitsById = new Map();
    this.animationFrame = null;
    this.dragStartClient = null;
    this.dragStartCamera = null;
    this.dragPointerButton = null;
    this.totalDragDistance = 0;
    this.isPointerCaptured = false;
    this.isFollowingSelection = false;
    this.isDisposed = false;
    this.lastReportedVisibleUnitIds = [];

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
    this.disposeBodyMeshes();
    this.disposeVisuals();
    this.disposeScannerCones();
    this.unitBodyMaterial.dispose();
    this.renderer.dispose();
    this.container.innerHTML = '';
  }

  setSnapshot(snapshot: GalaxySnapshotDto | null, ownerOverlay: Record<string, unknown>, selectedControllableId: string, navigationTarget: WorldSceneNavigationTarget) {
    const snapshotChanged = this.snapshot !== snapshot;
    const ownerOverlayChanged = this.ownerOverlay !== ownerOverlay;
    const selectedControllableChanged = this.selectedControllableId !== selectedControllableId;
    const navigationTargetChanged = !sameNavigationTarget(this.selectedNavigationTarget, navigationTarget);

    this.snapshot = snapshot;
    this.ownerOverlay = normalizeOwnerOverlay(ownerOverlay);
    this.selectedControllableId = selectedControllableId;
    this.selectedNavigationTarget = navigationTarget;

    if (snapshotChanged) {
      this.teamColors.clear();

      if (snapshot) {
        for (const team of snapshot.teams) {
          this.teamColors.set(team.name, team.colorHex);
        }
      }
    }

    if (snapshotChanged || ownerOverlayChanged) {
      this.syncUnits();
    } else {
      if (selectedControllableChanged) {
        this.updateSelectionRing();
      }

      if (selectedControllableChanged || navigationTargetChanged) {
        this.updateNavigationMarker();
        this.updateNavigationPointer();
      }
    }

    this.requestRender();
  }

  focusOnSelection() {
    if (!this.getFocusSelectionUnit()) {
      return;
    }

    this.setFocusSelectionActive(true);
    this.updateFocusSelection();
    this.requestRender();
  }

  toggleFocusSelection() {
    this.setFocusSelectionActive(!this.isFollowingSelection);
    this.updateFocusSelection();
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

    if (this.isFollowingSelection && this.totalDragDistance >= 6) {
      this.setFocusSelectionActive(false);
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
    this.updateFocusSelection();
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

    this.updateFocusSelection();
    this.reportVisibleUnits();
    this.renderer.render(this.scene, this.camera);
    this.animationFrame = window.requestAnimationFrame(this.renderFrame);
  };

  private syncUnits() {
    const { staticUnits, dynamicUnits } = this.buildRenderableUnits();
    const staticUnitsChanged = !sameRenderableUnits(this.staticRenderableUnits, staticUnits);

    this.staticRenderableUnits = staticUnits;
    this.dynamicRenderableUnits = dynamicUnits;
    this.renderableUnits = [...dynamicUnits, ...staticUnits];
    this.renderableUnitsById.clear();
    for (const unit of this.renderableUnits) {
      this.renderableUnitsById.set(unit.unitId, unit);
    }

    const liveDirectionalIds = new Set<string>();
    for (const unit of dynamicUnits) {
      if (isDirectionalKind(unit.renderKind)) {
        liveDirectionalIds.add(unit.unitId);
      }
    }

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

    if (staticUnitsChanged) {
      this.syncBodyInstances(this.staticBodies, staticUnits);
    }

    this.syncBodyInstances(this.dynamicBodies, dynamicUnits);

    for (let index = 0; index < dynamicUnits.length; index++) {
      const unit = dynamicUnits[index];
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

    this.updateSelectionRing();
    this.updateFocusSelection();
    this.updateNavigationMarker();
    this.updateNavigationPointer();
    this.updateScannerCones();
  }

  private updateFocusSelection() {
    if (!this.isFollowingSelection) {
      return;
    }

    const targetUnit = this.getFocusSelectionUnit();
    if (!targetUnit) {
      return;
    }

    this.camera.position.x = targetUnit.x;
    this.camera.position.y = -targetUnit.y;
  }

  private setFocusSelectionActive(value: boolean) {
    if (this.isFollowingSelection === value) {
      return;
    }

    this.isFollowingSelection = value;
    this.onFocusSelectionChanged?.(value);
  }

  private getFocusSelectionUnit() {
    return this.renderableUnitsById.get(this.selectedUnitId)
      ?? this.renderableUnitsById.get(this.selectedControllableId)
      ?? null;
  }

  private buildRenderableUnits() {
    if (!this.snapshot) {
      this.lastSeenTraces.clear();
      return {
        staticUnits: [...this.debugUnits],
        dynamicUnits: [] as NormalizedUnit[],
      };
    }

    const deadControllableIds = new Set<string>();
    for (const controllable of this.snapshot.controllables) {
      if (!getControllableAliveState(controllable, this.ownerOverlay[controllable.controllableId])) {
        deadControllableIds.add(controllable.controllableId);
      }
    }

    const staticUnits: RawRenderableUnit[] = [];
    const dynamicUnits: RawRenderableUnit[] = [];
    const liveUnitIds = new Set<string>();

    for (const unit of this.snapshot.units) {
      if (deadControllableIds.has(unit.unitId)) {
        continue;
      }

      if (unit.isStatic) {
        staticUnits.push(unit);
      } else {
        dynamicUnits.push(unit);
      }
      liveUnitIds.add(unit.unitId);
    }

    for (const controllable of this.snapshot.controllables) {
      if (deadControllableIds.has(controllable.controllableId)) {
        continue;
      }

      const overlay = this.ownerOverlay[controllable.controllableId];
      if (overlay) {
        dynamicUnits.push({
          unitId: controllable.controllableId,
          clusterId: numberValue(overlay.clusterId, 0),
          kind: stringValue(overlay.kind, 'controllable_marker'),
          isStatic: false,
          isSeen: true,
          lastSeenTick: 0,
          x: numberValue((overlay.position as OwnerOverlayState | undefined)?.x, 0),
          y: numberValue((overlay.position as OwnerOverlayState | undefined)?.y, 0),
          angle: numberValue((overlay.position as OwnerOverlayState | undefined)?.angle, 0),
          radius: numberValue(overlay.radius, getFallbackRenderRadius(normalizeKind(stringValue(overlay.kind, 'controllable_marker')))),
          teamName: controllable.teamName,
        });
        liveUnitIds.add(controllable.controllableId);
        continue;
      }

      if (liveUnitIds.has(controllable.controllableId)) {
        continue;
      }

      dynamicUnits.push({
        unitId: controllable.controllableId,
        clusterId: 0,
        kind: 'controllable_marker',
        isStatic: false,
        isSeen: true,
        lastSeenTick: 0,
        x: 0,
        y: 0,
        angle: 0,
        radius: getFallbackRenderRadius('ship'),
        teamName: controllable.teamName,
      });
      liveUnitIds.add(controllable.controllableId);
    }

    const visibleStaticUnits = this.buildNormalizedUnits(staticUnits);
    const visibleDynamicUnits = this.buildNormalizedUnits(dynamicUnits);
    const visibleUnits = [...visibleStaticUnits, ...visibleDynamicUnits];

    return {
      staticUnits: [...visibleStaticUnits, ...this.debugUnits],
      dynamicUnits: [...visibleDynamicUnits, ...this.captureLostUnitTraces(visibleUnits)],
    };
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
      visual.heading.position.set(unit.x, -unit.y, 0);
      visual.heading.rotation.z = toSceneRotation(unit.angle);
      headingMaterial.color.copy(bodyColor);
      headingMaterial.opacity = isUnseenDynamicUnit(unit) ? 0.34 : 0.8;
      const headingScale = isShipKind(unit.renderKind) ? scale * 0.85 : scale * 1.8;
      visual.heading.scale.set(headingScale, headingScale, 1);
    }
  }

  private updateBodyInstance(bodies: BodyMeshResources, index: number, unit: NormalizedUnit) {
    const scale = getRenderScale(unit);
    const bodyColor = this.tempColor.set(getColorForUnit(unit, this.teamColors));

    this.tempBodyTransform.position.set(unit.x, -unit.y, 0);
    this.tempBodyTransform.rotation.set(0, 0, toSceneRotation(unit.angle));
    this.tempBodyTransform.scale.set(scale, scale, 1);
    this.tempBodyTransform.updateMatrix();

    bodies.mesh.setMatrixAt(index, this.tempBodyTransform.matrix);
    bodies.colorAttribute.setXYZ(index, bodyColor.r, bodyColor.g, bodyColor.b);
    bodies.kindAttribute.setX(index, getShaderKindCode(unit.renderKind));
    bodies.opacityAttribute.setX(index, getOpacityForUnit(unit));
  }

  private updateSelectionRing() {
    const selectedUnit = this.renderableUnitsById.get(this.selectedUnitId)
      ?? this.renderableUnitsById.get(this.selectedControllableId);
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

  private updateNavigationPointer() {
    const navigationPointer = readNavigationPointer(this.ownerOverlay, this.selectedControllableId);
    if (!navigationPointer) {
      this.navigationPointer.visible = false;
      return;
    }

    const vectorLength = Math.hypot(navigationPointer.vectorX, navigationPointer.vectorY);
    if (vectorLength <= 0.0001) {
      this.navigationPointer.visible = false;
      return;
    }

    const selectedUnit = this.renderableUnitsById.get(this.selectedControllableId);
    const worldUnitsPerPixel = this.getWorldUnitsPerPixel();
    const headSize = Math.max(10, worldUnitsPerPixel * 18);
    const startOffset = Math.max(selectedUnit?.renderRadius ?? 0, worldUnitsPerPixel * 12);
    const displayLength = Math.max(vectorLength, worldUnitsPerPixel * 56);
    const unitX = navigationPointer.vectorX / vectorLength;
    const unitY = navigationPointer.vectorY / vectorLength;
    const normalX = -unitY;
    const normalY = unitX;
    const startX = navigationPointer.startX + unitX * startOffset;
    const startY = navigationPointer.startY + unitY * startOffset;
    const endX = navigationPointer.startX + unitX * (startOffset + displayLength);
    const endY = navigationPointer.startY + unitY * (startOffset + displayLength);
    const clampedHeadSize = Math.min(headSize, displayLength * 0.45);
    const headBaseX = endX - unitX * clampedHeadSize;
    const headBaseY = endY - unitY * clampedHeadSize;
    const wingSize = clampedHeadSize * 0.55;

    this.navigationPointer.geometry.setFromPoints([
      new THREE.Vector3(startX, -startY, 4),
      new THREE.Vector3(endX, -endY, 4),
      new THREE.Vector3(headBaseX + normalX * wingSize, -headBaseY - normalY * wingSize, 4),
      new THREE.Vector3(endX, -endY, 4),
      new THREE.Vector3(headBaseX - normalX * wingSize, -headBaseY + normalY * wingSize, 4),
      new THREE.Vector3(endX, -endY, 4),
    ]);
    this.navigationPointer.visible = true;
  }

  private updateScannerCones() {
    const activeScanners = new Map<string, {
      unit: NormalizedUnit;
      scannerState: OwnerOverlayState;
      teamColor: string;
    }>();

    for (const [controllableId, overlay] of Object.entries(this.ownerOverlay)) {
      const scannerState = overlay.scanner as OwnerOverlayState | undefined;
      if (!scannerState || scannerState.active !== true) continue;

      const unit = this.renderableUnitsById.get(controllableId);
      if (!unit) continue;

      const teamColor = this.teamColors.get(unit.teamName ?? '') ?? '#7cb3dd';
      activeScanners.set(controllableId, { unit, scannerState, teamColor });
    }

    for (const [cId, cone] of this.scannerCones) {
      if (!activeScanners.has(cId)) {
        this.root.remove(cone.mesh);
        cone.mesh.geometry.dispose();
        cone.mesh.material.dispose();
        this.scannerCones.delete(cId);
      }
    }

    for (const [cId, { unit, scannerState, teamColor }] of activeScanners) {
      const currentWidth = numberValue(scannerState.currentWidth, 90);
      const currentLength = numberValue(scannerState.currentLength, 200);
      const currentAngle = numberValue(scannerState.currentAngle, 0);
      const targetWidth = numberValue(scannerState.targetWidth, currentWidth);
      const targetLength = numberValue(scannerState.targetLength, currentLength);

      const halfWidthRad = (currentWidth / 2) * (Math.PI / 180);
      const targetHalfWidthRad = (targetWidth / 2) * (Math.PI / 180);
      const normalizedTargetLength = currentLength > 0 ? targetLength / currentLength : 1;

      let cone = this.scannerCones.get(cId);
      if (!cone) {
        cone = createScannerConeMesh(cId, halfWidthRad, currentLength, targetHalfWidthRad, normalizedTargetLength, teamColor);
        this.scannerCones.set(cId, cone);
        this.root.add(cone.mesh);
      }

      const uniforms = cone.mesh.material.uniforms;
      uniforms.time.value = this.unitBodyMaterial.uniforms.time.value;
      uniforms.halfWidthRad.value = halfWidthRad;
      uniforms.coneLength.value = currentLength;
      uniforms.targetHalfWidthRad.value = targetHalfWidthRad;
      uniforms.targetLength.value = normalizedTargetLength;
      (uniforms.color.value as THREE.Color).set(teamColor);

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

  private reportVisibleUnits() {
    if (!this.onVisibleUnitsChanged) {
      return;
    }

    const visibleWorldWidth = (this.camera.right - this.camera.left) / this.camera.zoom;
    const visibleWorldHeight = (this.camera.top - this.camera.bottom) / this.camera.zoom;
    const minX = this.camera.position.x - visibleWorldWidth / 2;
    const maxX = this.camera.position.x + visibleWorldWidth / 2;
    const minY = -this.camera.position.y - visibleWorldHeight / 2;
    const maxY = -this.camera.position.y + visibleWorldHeight / 2;

    const visibleUnitIds = this.renderableUnits
      .filter((unit) => {
        const padding = unit.renderRadius;
        return unit.x + padding >= minX
          && unit.x - padding <= maxX
          && unit.y + padding >= minY
          && unit.y - padding <= maxY;
      })
      .map((unit) => unit.unitId)
      .sort();

    if (
      visibleUnitIds.length === this.lastReportedVisibleUnitIds.length
      && visibleUnitIds.every((unitId, index) => unitId === this.lastReportedVisibleUnitIds[index])
    ) {
      return;
    }

    this.lastReportedVisibleUnitIds = visibleUnitIds;
    this.onVisibleUnitsChanged(visibleUnitIds);
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
    this.navigationPointer.geometry.dispose();
    (this.navigationPointer.material as THREE.Material).dispose();
  }

  private disposeBodyMeshes() {
    this.staticBodies.mesh.geometry.dispose();
    this.dynamicBodies.mesh.geometry.dispose();
  }

  private disposeScannerCones() {
    for (const [, cone] of this.scannerCones) {
      this.root.remove(cone.mesh);
      cone.mesh.geometry.dispose();
      cone.mesh.material.dispose();
    }
    this.scannerCones.clear();
  }

  private ensureBodyMeshCapacity(bodies: BodyMeshResources, requiredCount: number) {
    if (requiredCount <= bodies.colorAttribute.count) {
      return bodies;
    }

    const nextCapacity = Math.max(requiredCount, Math.ceil(bodies.colorAttribute.count * 1.5), 1);
    this.scene.remove(bodies.mesh);
    bodies.mesh.geometry.dispose();

    const nextBodies = createUnitBodyMesh(nextCapacity, this.unitBodyMaterial);
    this.scene.add(nextBodies.mesh);
    return nextBodies;
  }

  private syncBodyInstances(bodies: BodyMeshResources, units: NormalizedUnit[]) {
    const nextBodies = this.ensureBodyMeshCapacity(bodies, units.length);
    if (bodies === this.staticBodies) {
      this.staticBodies = nextBodies;
    } else if (bodies === this.dynamicBodies) {
      this.dynamicBodies = nextBodies;
    }

    for (let index = 0; index < units.length; index++) {
      this.updateBodyInstance(nextBodies, index, units[index]);
    }

    nextBodies.mesh.count = units.length;
    nextBodies.mesh.instanceMatrix.needsUpdate = true;
    nextBodies.colorAttribute.needsUpdate = true;
    nextBodies.kindAttribute.needsUpdate = true;
    nextBodies.opacityAttribute.needsUpdate = true;
  }

  private buildNormalizedUnits(units: RawRenderableUnit[]): NormalizedUnit[] {
    const normalizedUnits: NormalizedUnit[] = [];

    for (const unit of units) {
      normalizedUnits.push(this.normalizeUnit(unit, false));
    }

    return normalizedUnits;
  }

  private captureLostUnitTraces(visibleUnits: NormalizedUnit[]): NormalizedUnit[] {
    const visibleUnitIds = new Set<string>();
    for (const unit of visibleUnits) {
      visibleUnitIds.add(unit.unitId);
    }

    for (const unitId of this.lastSeenTraces.keys()) {
      if (visibleUnitIds.has(unitId)) {
        this.lastSeenTraces.delete(unitId);
      }
    }

    for (const unit of this.renderableUnits) {
      if (visibleUnitIds.has(unit.unitId) || !shouldPersistTraceForUnit(unit)) {
        continue;
      }

      this.lastSeenTraces.set(unit.unitId, this.normalizeUnit(unit, true));
    }

    return Array.from(this.lastSeenTraces.values());
  }

  private normalizeUnit(unit: RawRenderableUnit | NormalizedUnit, isTrace: boolean) {
    const cacheKey = `${unit.unitId}:${isTrace ? 'trace' : 'base'}`;
    const signature = buildRenderableUnitSignature(unit, isTrace);
    const cached = this.normalizedUnitsByKey.get(cacheKey);
    if (cached && cached.signature === signature) {
      return cached.unit;
    }

    const renderKind = normalizeKind(unit.kind);
    const normalizedUnit: NormalizedUnit = {
      unitId: unit.unitId,
      clusterId: unit.clusterId,
      kind: unit.kind,
      isStatic: unit.isStatic,
      isSeen: unit.isSeen,
      lastSeenTick: unit.lastSeenTick,
      x: unit.x,
      y: unit.y,
      angle: unit.angle,
      radius: unit.radius,
      teamName: unit.teamName,
      isTrace,
      renderKind,
      renderRadius: getRenderRadius(unit.radius, renderKind),
    };
    this.normalizedUnitsByKey.set(cacheKey, { signature, unit: normalizedUnit });
    return normalizedUnit;
  }
}

export function formatTeamAccent(teamName: string | undefined, teams: TeamSnapshotDto[]) {
  if (!teamName) {
    return '#d7ecff';
  }

  return teams.find((team) => team.name === teamName)?.colorHex ?? '#d7ecff';
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

export function readNavigationPointer(ownerOverlay: Record<string, unknown>, controllableId: string): WorldSceneNavigationPointer {
  const overlay = ownerOverlay[controllableId];
  if (!overlay || typeof overlay !== 'object') {
    return null;
  }

  const overlayState = overlay as Record<string, unknown>;
  const navigation = overlayState.navigation;
  if (!navigation || typeof navigation !== 'object') {
    return null;
  }

  const navigationState = navigation as Record<string, unknown>;
  if (navigationState.active !== true) {
    return null;
  }

  const position = overlayState.position;
  const positionState = position && typeof position === 'object'
    ? position as Record<string, unknown>
    : null;
  const fallbackStartX = positionState ? numberValue(positionState.x, 0) : 0;
  const fallbackStartY = positionState ? numberValue(positionState.y, 0) : 0;
  let vectorX = numberValue(navigationState.vectorX, Number.NaN);
  let vectorY = numberValue(navigationState.vectorY, Number.NaN);
  let endX = numberValue(navigationState.pointerX, Number.NaN);
  let endY = numberValue(navigationState.pointerY, Number.NaN);

  if (!Number.isFinite(vectorX) || !Number.isFinite(vectorY)) {
    const targetX = numberValue(navigationState.targetX, Number.NaN);
    const targetY = numberValue(navigationState.targetY, Number.NaN);
    if (Number.isFinite(targetX) && Number.isFinite(targetY)) {
      vectorX = targetX - fallbackStartX;
      vectorY = targetY - fallbackStartY;
    }
  }

  if (!Number.isFinite(endX) || !Number.isFinite(endY)) {
    if (!Number.isFinite(vectorX) || !Number.isFinite(vectorY)) {
      return null;
    }

    endX = fallbackStartX + vectorX;
    endY = fallbackStartY + vectorY;
  }

  const startX = positionState ? numberValue(positionState.x, endX - vectorX) : endX - vectorX;
  const startY = positionState ? numberValue(positionState.y, endY - vectorY) : endY - vectorY;

  return {
    startX,
    startY,
    endX,
    endY,
    vectorX,
    vectorY,
  };
}

function normalizeOwnerOverlay(ownerOverlay: Record<string, unknown>) {
  return ownerOverlay && typeof ownerOverlay === 'object'
    ? ownerOverlay as Record<string, OwnerOverlayState>
    : {};
}

function sameNavigationTarget(left: WorldSceneNavigationTarget, right: WorldSceneNavigationTarget) {
  if (left === right) {
    return true;
  }

  if (!left || !right) {
    return left === right;
  }

  return left.x === right.x && left.y === right.y;
}

function sameRenderableUnits(left: NormalizedUnit[], right: NormalizedUnit[]) {
  if (left === right) {
    return true;
  }

  if (left.length !== right.length) {
    return false;
  }

  for (let index = 0; index < left.length; index++) {
    const leftUnit = left[index];
    const rightUnit = right[index];
    if (leftUnit === rightUnit) {
      continue;
    }

    if (
      leftUnit.unitId !== rightUnit.unitId
      || leftUnit.clusterId !== rightUnit.clusterId
      || leftUnit.kind !== rightUnit.kind
      || leftUnit.isStatic !== rightUnit.isStatic
      || leftUnit.isSeen !== rightUnit.isSeen
      || leftUnit.lastSeenTick !== rightUnit.lastSeenTick
      || leftUnit.x !== rightUnit.x
      || leftUnit.y !== rightUnit.y
      || leftUnit.angle !== rightUnit.angle
      || leftUnit.radius !== rightUnit.radius
      || leftUnit.teamName !== rightUnit.teamName
      || leftUnit.isTrace !== rightUnit.isTrace
      || leftUnit.renderKind !== rightUnit.renderKind
      || leftUnit.renderRadius !== rightUnit.renderRadius
    ) {
      return false;
    }
  }

  return true;
}

function buildRenderableUnitSignature(unit: RawRenderableUnit | NormalizedUnit, isTrace: boolean) {
  return [
    unit.unitId,
    unit.clusterId,
    unit.kind,
    unit.isStatic ? 1 : 0,
    unit.isSeen ? 1 : 0,
    unit.lastSeenTick,
    unit.x,
    unit.y,
    unit.angle,
    unit.radius,
    unit.teamName ?? '',
    isTrace ? 1 : 0,
  ].join('|');
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

function stringValue(value: unknown, fallback: string) {
  return typeof value === 'string' && value.length > 0 ? value : fallback;
}
