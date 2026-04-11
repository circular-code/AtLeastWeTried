import * as THREE from 'three';
import { DEFAULT_VIEW_HALF_HEIGHT, DEBUG_SUN_COUNT, MIN_PICK_RADIUS_PX, PICK_RADIUS_PADDING_PX } from './constants';
import { createUnitBodyMaterial, createUnitBodyMesh, createDebugSuns } from './unitBody';
import { normalizeKind, getRenderRadius, getFallbackRenderRadius, getRenderScale, getColorForUnit, getShaderKindCode, getOpacityForUnit, toSceneRotation, isDirectionalKind, isShipKind, getHeadingPoints, shouldPersistTraceForUnit, isUnseenDynamicUnit } from './unitVisuals';
import { createScannerConeMesh } from './scannerCone';
import { createSelectionRing } from './selectionRing';
import { createNavigationMarker } from './navigationMarker';
import { createNavigationPointer } from './navigationPointer';
import { createTrackedTargetVisual, disposeTrackedTargetVisual } from './trackOverlay';
import { createGrid, createGlowField, createGravityStrengthGrid } from './grid';
import { createShipStatusVisual, disposeShipStatusVisual, hideShipStatusVisual, updateShipStatusVisual } from './shipStatusOverlay';
import { isPlayerShipUnitKind, isShortLivedTransientUnitKind } from '../lib/unitKinds';
export class WorldScene {
    container;
    onSelection;
    onNavigationTargetRequested;
    onFreeFireRequested;
    onVisibleUnitsChanged;
    onFocusSelectionChanged;
    onThrustWheelRequested;
    renderer;
    scene;
    camera;
    grid;
    gravityStrengthGrid;
    root;
    staticBodies;
    dynamicBodies;
    selectedRing;
    tacticalTargetRing;
    selectedCenterDot;
    navigationMarker;
    navigationPointer;
    trackedTargetVisuals;
    navigationPathPreview;
    navigationSearchPreview;
    trajectoryPreview;
    unseenTrajectoryPreview;
    unitBodyMaterial;
    resizeObserver;
    unitVisuals;
    shipStatusVisuals;
    teamColors;
    debugUnits;
    lastSeenTraces;
    normalizedUnitsByKey;
    tempBodyTransform;
    tempColor;
    staticRenderableUnits;
    dynamicRenderableUnits;
    renderableUnits;
    renderableUnitsById;
    snapshot;
    ownerOverlay;
    selectedControllableId;
    selectedUnitId;
    selectedNavigationTarget;
    tacticalTargetUnitId;
    trackedUnitColors;
    customShipColors;
    scannerCones;
    animationFrame;
    dragStartClient;
    dragStartCamera;
    dragPointerButton;
    totalDragDistance;
    isPointerCaptured;
    isFollowingSelection;
    isDisposed;
    lastReportedVisibleUnitIds;
    lastGravityGridViewSignature;
    lastGravityGridSourceSignature;
    constructor(container, options = {}) {
        this.container = container;
        this.onSelection = options.onSelection;
        this.onNavigationTargetRequested = options.onNavigationTargetRequested;
        this.onFreeFireRequested = options.onFreeFireRequested;
        this.onVisibleUnitsChanged = options.onVisibleUnitsChanged;
        this.onFocusSelectionChanged = options.onFocusSelectionChanged;
        this.onThrustWheelRequested = options.onThrustWheelRequested;
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        this.renderer.setClearColor(0x050811, 1);
        this.scene = new THREE.Scene();
        this.camera = new THREE.OrthographicCamera(-DEFAULT_VIEW_HALF_HEIGHT, DEFAULT_VIEW_HALF_HEIGHT, DEFAULT_VIEW_HALF_HEIGHT, -DEFAULT_VIEW_HALF_HEIGHT, 0.1, 5000);
        this.camera.position.set(0, 0, 100);
        this.camera.lookAt(0, 0, 0);
        this.grid = createGrid();
        this.gravityStrengthGrid = createGravityStrengthGrid();
        this.root = new THREE.Group();
        this.unitBodyMaterial = createUnitBodyMaterial();
        this.staticBodies = createUnitBodyMesh(Math.max(DEBUG_SUN_COUNT, 1), this.unitBodyMaterial);
        this.dynamicBodies = createUnitBodyMesh(1, this.unitBodyMaterial);
        this.selectedRing = createSelectionRing();
        this.tacticalTargetRing = createSelectionRing();
        const tacticalTargetRingMaterial = this.tacticalTargetRing.material;
        tacticalTargetRingMaterial.color.setHex(0xff3b3b);
        tacticalTargetRingMaterial.opacity = 0.98;
        tacticalTargetRingMaterial.depthTest = false;
        tacticalTargetRingMaterial.depthWrite = false;
        this.tacticalTargetRing.renderOrder = 45;
        this.selectedCenterDot = new THREE.Mesh(new THREE.CircleGeometry(1, 18), new THREE.MeshBasicMaterial({
            color: 0x000000,
            transparent: true,
            opacity: 0.95,
            depthTest: false,
            depthWrite: false,
        }));
        this.navigationMarker = createNavigationMarker();
        this.navigationPointer = createNavigationPointer();
        this.trackedTargetVisuals = new Map();
        this.navigationPathPreview = new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({
            color: 0x7cd0ff,
            transparent: true,
            opacity: 0.94,
            depthTest: false,
            depthWrite: false,
        }));
        this.navigationSearchPreview = new THREE.LineSegments(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({
            color: 0x4f6f8f,
            transparent: true,
            opacity: 0.4,
            depthTest: false,
            depthWrite: false,
        }));
        this.trajectoryPreview = new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({
            color: 0xff9933,
            transparent: true,
            opacity: 0.8,
            depthTest: false,
            depthWrite: false,
        }));
        this.unseenTrajectoryPreview = new THREE.LineSegments(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({
            color: 0xb7d8ff,
            transparent: true,
            opacity: 0.82,
            depthTest: false,
            depthWrite: false,
        }));
        this.navigationPathPreview.visible = false;
        this.navigationPathPreview.frustumCulled = false;
        this.navigationPathPreview.renderOrder = 39;
        this.navigationSearchPreview.visible = false;
        this.navigationSearchPreview.frustumCulled = false;
        this.navigationSearchPreview.renderOrder = 38;
        this.trajectoryPreview.visible = false;
        this.trajectoryPreview.frustumCulled = false;
        this.trajectoryPreview.renderOrder = 42;
        this.unseenTrajectoryPreview.visible = false;
        this.unseenTrajectoryPreview.frustumCulled = false;
        this.unseenTrajectoryPreview.renderOrder = 37;
        this.scannerCones = new Map();
        this.scene.add(this.grid);
        this.scene.add(this.gravityStrengthGrid.group);
        this.scene.add(this.staticBodies.mesh);
        this.scene.add(this.dynamicBodies.mesh);
        this.scene.add(this.root);
        this.scene.add(createGlowField());
        this.root.add(this.selectedRing);
        this.root.add(this.tacticalTargetRing);
        this.root.add(this.selectedCenterDot);
        this.root.add(this.navigationMarker);
        this.root.add(this.navigationPointer);
        this.root.add(this.navigationSearchPreview);
        this.root.add(this.navigationPathPreview);
        this.root.add(this.unseenTrajectoryPreview);
        this.root.add(this.trajectoryPreview);
        this.snapshot = null;
        this.ownerOverlay = {};
        this.selectedControllableId = '';
        this.selectedUnitId = '';
        this.selectedNavigationTarget = null;
        this.tacticalTargetUnitId = '';
        this.trackedUnitColors = {};
        this.customShipColors = {};
        this.unitVisuals = new Map();
        this.shipStatusVisuals = new Map();
        this.teamColors = new Map();
        this.debugUnits = createDebugSuns();
        this.lastSeenTraces = new Map();
        this.normalizedUnitsByKey = new Map();
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
        this.lastGravityGridViewSignature = '';
        this.lastGravityGridSourceSignature = '';
        this.selectedCenterDot.visible = false;
        this.renderer.domElement.classList.add('world-canvas');
        this.container.appendChild(this.renderer.domElement);
        this.container.addEventListener('pointerdown', this.handlePointerDown);
        this.container.addEventListener('pointermove', this.handlePointerMove);
        this.container.addEventListener('pointerup', this.handlePointerUp);
        this.container.addEventListener('pointerleave', this.handlePointerLeave);
        this.container.addEventListener('contextmenu', this.handleContextMenu);
        this.container.addEventListener('wheel', this.handleWheel, { passive: false });
        window.addEventListener('keydown', this.handleKeyDown);
        this.resizeObserver = new ResizeObserver(() => {
            this.resize();
            this.requestRender();
        });
        this.resizeObserver.observe(this.container);
        this.resize();
        this.syncUnits();
        this.requestRender();
    }
    setTrackedUnits(trackedUnitColors) {
        if (sameTrackedUnitColors(this.trackedUnitColors, trackedUnitColors)) {
            return;
        }
        this.trackedUnitColors = { ...trackedUnitColors };
        this.syncTrackedTargetVisuals();
        this.updateTrackedTargets();
        this.requestRender();
    }
    setCustomShipColors(customShipColors) {
        if (sameColorAssignments(this.customShipColors, customShipColors)) {
            return;
        }
        this.customShipColors = { ...customShipColors };
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
        window.removeEventListener('keydown', this.handleKeyDown);
        this.disposeBodyMeshes();
        this.disposeVisuals();
        this.disposeTrackedTargetVisuals();
        this.disposeScannerCones();
        this.gravityStrengthGrid.dispose();
        this.unitBodyMaterial.dispose();
        this.renderer.dispose();
        this.container.innerHTML = '';
    }
    setSnapshot(snapshot, ownerOverlay, selectedControllableId, navigationTarget, tacticalTargetUnitId) {
        const snapshotChanged = this.snapshot !== snapshot;
        const ownerOverlayChanged = this.ownerOverlay !== ownerOverlay;
        const selectedControllableChanged = this.selectedControllableId !== selectedControllableId;
        const navigationTargetChanged = !sameNavigationTarget(this.selectedNavigationTarget, navigationTarget);
        const tacticalTargetChanged = this.tacticalTargetUnitId !== tacticalTargetUnitId;
        this.snapshot = snapshot;
        this.ownerOverlay = normalizeOwnerOverlay(ownerOverlay);
        this.selectedControllableId = selectedControllableId;
        this.selectedNavigationTarget = navigationTarget;
        this.tacticalTargetUnitId = tacticalTargetUnitId;
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
        }
        else {
            if (selectedControllableChanged) {
                this.updateSelectionRing();
            }
            if (selectedControllableChanged || tacticalTargetChanged) {
                this.updateTacticalTargetRing();
            }
            if (selectedControllableChanged || navigationTargetChanged) {
                this.updateNavigationMarker();
                this.updateNavigationPointer();
                this.updateNavigationPathPreview();
                this.updateNavigationSearchPreview();
                this.updateTrajectoryPreview();
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
    jumpToUnit(unitId) {
        if (!unitId) {
            return;
        }
        const targetUnit = this.renderableUnitsById.get(unitId);
        if (!targetUnit) {
            return;
        }
        this.camera.position.x = targetUnit.x;
        this.camera.position.y = -targetUnit.y;
        this.requestRender();
    }
    handlePointerDown = (event) => {
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
    handlePointerMove = (event) => {
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
    handlePointerUp = (event) => {
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
        if (pointerButton === 0 && event.shiftKey && event.ctrlKey) {
            const unit = this.findNearestUnit(worldPosition.x, worldPosition.y);
            this.onFreeFireRequested?.({
                worldX: worldPosition.x,
                worldY: worldPosition.y,
                unitId: unit?.unitId,
                kind: unit?.kind,
                teamName: unit?.teamName,
            });
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
                direct: event.shiftKey,
            });
            return;
        }
        const unit = this.findNearestUnit(worldPosition.x, worldPosition.y);
        this.selectedUnitId = unit?.unitId ?? '';
        this.updateSelectionRing();
        this.updateTacticalTargetRing();
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
    handlePointerLeave = (event) => {
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
    handleContextMenu = (event) => {
        event.preventDefault();
    };
    handleWheel = (event) => {
        event.preventDefault();
        if (event.shiftKey) {
            this.onThrustWheelRequested?.(normalizeWheelDelta(event));
            return;
        }
        const worldBeforeZoom = this.clientToWorld(event.clientX, event.clientY);
        const zoomFactor = Math.exp(-event.deltaY * 0.0012);
        this.camera.zoom = THREE.MathUtils.clamp(this.camera.zoom * zoomFactor, 0.12, 8);
        this.camera.updateProjectionMatrix();
        const worldAfterZoom = this.clientToWorld(event.clientX, event.clientY);
        this.camera.position.x += worldBeforeZoom.x - worldAfterZoom.x;
        this.camera.position.y += worldBeforeZoom.y - worldAfterZoom.y;
        this.requestRender();
    };
    handleKeyDown = (event) => {
        if (event.code !== 'Space' || event.repeat || isEditableTarget(event.target)) {
            return;
        }
        const targetUnit = this.getFocusSelectionUnit();
        if (!targetUnit) {
            return;
        }
        event.preventDefault();
        this.camera.position.x = targetUnit.x;
        this.camera.position.y = -targetUnit.y;
        this.requestRender();
    };
    resize() {
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
    requestRender() {
        if (this.animationFrame !== null) {
            return;
        }
        this.animationFrame = window.requestAnimationFrame(this.renderFrame);
    }
    renderFrame = (timestamp) => {
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
        this.updateGravityStrengthGrid();
        this.updateTrackedTargets();
        this.reportVisibleUnits();
        this.renderer.render(this.scene, this.camera);
        this.animationFrame = window.requestAnimationFrame(this.renderFrame);
    };
    syncUnits() {
        const { staticUnits, dynamicUnits } = this.buildRenderableUnits();
        const staticUnitsChanged = !sameRenderableUnits(this.staticRenderableUnits, staticUnits);
        this.staticRenderableUnits = staticUnits;
        this.dynamicRenderableUnits = dynamicUnits;
        this.renderableUnits = [...dynamicUnits, ...staticUnits];
        this.renderableUnitsById.clear();
        for (const unit of this.renderableUnits) {
            this.renderableUnitsById.set(unit.unitId, unit);
        }
        const liveDirectionalIds = new Set();
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
        this.syncShipStatusVisuals(dynamicUnits);
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
        this.updateTacticalTargetRing();
        this.updateFocusSelection();
        this.updateNavigationMarker();
        this.updateNavigationPointer();
        this.updateNavigationPathPreview();
        this.updateNavigationSearchPreview();
        this.updateUnseenTrajectoryPreview();
        this.updateTrajectoryPreview();
        this.updateScannerCones();
        this.updateTrackedTargets();
    }
    syncShipStatusVisuals(units) {
        const liveStatusIds = new Set();
        for (const unit of units) {
            if (unit.isTrace || !isShipKind(unit.renderKind)) {
                continue;
            }
            const relation = this.getShipRelation(unit);
            if (!relation) {
                continue;
            }
            const overlayState = objectValue(this.ownerOverlay[unit.unitId]);
            const hullState = objectValue(overlayState?.hull);
            const shieldState = objectValue(overlayState?.shield);
            const hullMaximum = numberValue(hullState?.maximum, 0);
            const shieldMaximum = numberValue(shieldState?.maximum, 0);
            if (hullMaximum <= 0 && shieldMaximum <= 0) {
                continue;
            }
            let visual = this.shipStatusVisuals.get(unit.unitId);
            if (!visual) {
                visual = createShipStatusVisual();
                this.shipStatusVisuals.set(unit.unitId, visual);
                this.root.add(visual.group);
            }
            updateShipStatusVisual(visual, {
                x: unit.x,
                y: -unit.y,
                radius: unit.renderRadius,
                hullRatio: hullMaximum > 0 ? THREE.MathUtils.clamp(numberValue(hullState?.current, 0) / hullMaximum, 0, 1) : 0,
                shieldRatio: shieldMaximum > 0 ? THREE.MathUtils.clamp(numberValue(shieldState?.current, 0) / shieldMaximum, 0, 1) : 0,
                relation,
            });
            liveStatusIds.add(unit.unitId);
        }
        for (const [unitId, visual] of this.shipStatusVisuals.entries()) {
            if (liveStatusIds.has(unitId)) {
                continue;
            }
            hideShipStatusVisual(visual);
            this.root.remove(visual.group);
            disposeShipStatusVisual(visual);
            this.shipStatusVisuals.delete(unitId);
        }
    }
    getShipRelation(unit) {
        const selectedTeamName = this.snapshot?.controllables.find((controllable) => controllable.controllableId === this.selectedControllableId)?.teamName;
        if (!selectedTeamName || !unit.teamName) {
            return null;
        }
        return unit.teamName === selectedTeamName ? 'friendly' : 'enemy';
    }
    updateFocusSelection() {
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
    setFocusSelectionActive(value) {
        if (this.isFollowingSelection === value) {
            return;
        }
        this.isFollowingSelection = value;
        this.onFocusSelectionChanged?.(value);
    }
    getFocusSelectionUnit() {
        return this.renderableUnitsById.get(this.selectedUnitId)
            ?? this.renderableUnitsById.get(this.selectedControllableId)
            ?? null;
    }
    buildRenderableUnits() {
        if (!this.snapshot) {
            this.lastSeenTraces.clear();
            this.normalizedUnitsByKey.clear();
            return {
                staticUnits: [...this.debugUnits],
                dynamicUnits: [],
            };
        }
        const deadControllableIds = new Set();
        for (const controllable of this.snapshot.controllables) {
            if (!getControllableAliveState(controllable, this.ownerOverlay[controllable.controllableId])) {
                deadControllableIds.add(controllable.controllableId);
            }
        }
        const staticUnits = [];
        const dynamicUnits = [];
        const liveUnitIds = new Set();
        for (const unit of this.snapshot.units) {
            if (deadControllableIds.has(unit.unitId)) {
                continue;
            }
            if (unit.isStatic) {
                staticUnits.push(unit);
            }
            else {
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
                const overlayUnit = {
                    unitId: controllable.controllableId,
                    clusterId: numberValue(overlay.clusterId, 0),
                    kind: stringValue(overlay.kind, 'controllable_marker'),
                    isStatic: false,
                    isSeen: true,
                    lastSeenTick: 0,
                    x: numberValue(overlay.position?.x, 0),
                    y: numberValue(overlay.position?.y, 0),
                    angle: numberValue(overlay.position?.angle, 0),
                    radius: numberValue(overlay.radius, getFallbackRenderRadius(normalizeKind(stringValue(overlay.kind, 'controllable_marker')))),
                    teamName: controllable.teamName,
                };
                const existingDynamicIndex = dynamicUnits.findIndex((unit) => unit.unitId === controllable.controllableId);
                if (existingDynamicIndex >= 0) {
                    dynamicUnits[existingDynamicIndex] = {
                        ...dynamicUnits[existingDynamicIndex],
                        ...overlayUnit,
                    };
                }
                else {
                    dynamicUnits.push(overlayUnit);
                }
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
        this.pruneShortLivedUnitCache(liveUnitIds);
        const visibleStaticUnits = this.buildNormalizedUnits(staticUnits);
        const visibleDynamicUnits = this.buildNormalizedUnits(dynamicUnits);
        const visibleUnits = [...visibleStaticUnits, ...visibleDynamicUnits];
        return {
            staticUnits: [...visibleStaticUnits, ...this.debugUnits],
            dynamicUnits: [...visibleDynamicUnits, ...this.captureLostUnitTraces(visibleUnits)],
        };
    }
    createVisual() {
        return {
            heading: new THREE.Line(new THREE.BufferGeometry().setFromPoints(getHeadingPoints('shot')), new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.8 })),
            instanceIndex: -1,
        };
    }
    updateVisual(visual, unit, instanceIndex) {
        const scale = getRenderScale(unit);
        const bodyColor = this.tempColor.set(getColorForUnit(unit, this.teamColors, this.customShipColors));
        visual.instanceIndex = instanceIndex;
        if (visual.heading) {
            const headingMaterial = visual.heading.material;
            visual.heading.position.set(unit.x, -unit.y, 0);
            visual.heading.rotation.z = toSceneRotation(unit.angle);
            headingMaterial.color.copy(bodyColor);
            headingMaterial.opacity = isUnseenDynamicUnit(unit) ? 0.34 : 0.8;
            const headingScale = isShipKind(unit.renderKind) ? scale * 0.85 : scale * 1.8;
            visual.heading.scale.set(headingScale, headingScale, 1);
        }
    }
    updateBodyInstance(bodies, index, unit) {
        const scale = getRenderScale(unit);
        const bodyColor = this.tempColor.set(getColorForUnit(unit, this.teamColors, this.customShipColors));
        this.tempBodyTransform.position.set(unit.x, -unit.y, 0);
        this.tempBodyTransform.rotation.set(0, 0, toSceneRotation(unit.angle));
        this.tempBodyTransform.scale.set(scale, scale, 1);
        this.tempBodyTransform.updateMatrix();
        bodies.mesh.setMatrixAt(index, this.tempBodyTransform.matrix);
        bodies.colorAttribute.setXYZ(index, bodyColor.r, bodyColor.g, bodyColor.b);
        bodies.kindAttribute.setX(index, getShaderKindCode(unit.renderKind));
        bodies.opacityAttribute.setX(index, getOpacityForUnit(unit));
    }
    updateSelectionRing() {
        const selectedUnit = this.renderableUnitsById.get(this.selectedUnitId)
            ?? this.renderableUnitsById.get(this.selectedControllableId);
        if (!selectedUnit) {
            this.selectedRing.visible = false;
            this.selectedCenterDot.visible = false;
            return;
        }
        const selectionRadius = Math.max(selectedUnit.renderRadius * 1.18, 4);
        this.selectedRing.visible = true;
        this.selectedRing.position.set(selectedUnit.x, -selectedUnit.y, 0);
        this.selectedRing.scale.set(selectionRadius / 1.25, selectionRadius / 1.25, 1);
        if (selectedUnit.unitId === this.selectedControllableId) {
            const dotRadius = Math.max(selectedUnit.renderRadius * 0.16, 0.9) + this.getWorldUnitsPerPixel() * 2;
            this.selectedCenterDot.visible = true;
            this.selectedCenterDot.position.set(selectedUnit.x, -selectedUnit.y, 2);
            this.selectedCenterDot.scale.set(dotRadius, dotRadius, 1);
        }
        else {
            this.selectedCenterDot.visible = false;
        }
    }
    updateTacticalTargetRing() {
        if (!this.tacticalTargetUnitId) {
            this.tacticalTargetRing.visible = false;
            return;
        }
        const targetUnit = this.renderableUnitsById.get(this.tacticalTargetUnitId);
        if (!targetUnit) {
            this.tacticalTargetRing.visible = false;
            return;
        }
        const targetRadius = Math.max(targetUnit.renderRadius * 1.35, 5.5);
        this.tacticalTargetRing.visible = true;
        this.tacticalTargetRing.position.set(targetUnit.x, -targetUnit.y, 0.1);
        this.tacticalTargetRing.scale.set(targetRadius / 1.25, targetRadius / 1.25, 1);
    }
    updateNavigationMarker() {
        if (!this.selectedNavigationTarget) {
            this.navigationMarker.visible = false;
            return;
        }
        const markerScale = Math.max(10, this.getWorldUnitsPerPixel() * 22);
        this.navigationMarker.visible = true;
        this.navigationMarker.position.set(this.selectedNavigationTarget.x, -this.selectedNavigationTarget.y, 0);
        this.navigationMarker.scale.set(markerScale, markerScale, 1);
    }
    updateNavigationPointer() {
        const navigationPointer = readNavigationPointer(this.ownerOverlay, this.selectedControllableId);
        if (!navigationPointer) {
            this.navigationPointer.visible = false;
            return;
        }
        if (!this.isSelectedControllableAlive()) {
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
        this.replaceLineGeometry(this.navigationPointer, [
            new THREE.Vector3(startX, -startY, 4),
            new THREE.Vector3(endX, -endY, 4),
            new THREE.Vector3(headBaseX + normalX * wingSize, -headBaseY - normalY * wingSize, 4),
            new THREE.Vector3(endX, -endY, 4),
            new THREE.Vector3(headBaseX - normalX * wingSize, -headBaseY + normalY * wingSize, 4),
            new THREE.Vector3(endX, -endY, 4),
        ]);
        this.navigationPointer.visible = true;
    }
    syncTrackedTargetVisuals() {
        for (const [unitId, visual] of this.trackedTargetVisuals.entries()) {
            if (this.trackedUnitColors[unitId] === visual.color) {
                continue;
            }
            this.root.remove(visual.line);
            this.root.remove(visual.marker);
            this.root.remove(visual.edgeIndicator);
            disposeTrackedTargetVisual(visual);
            this.trackedTargetVisuals.delete(unitId);
        }
        for (const [unitId, color] of Object.entries(this.trackedUnitColors)) {
            if (this.trackedTargetVisuals.has(unitId)) {
                continue;
            }
            const visual = createTrackedTargetVisual(color);
            this.trackedTargetVisuals.set(unitId, visual);
            this.root.add(visual.line);
            this.root.add(visual.marker);
            this.root.add(visual.edgeIndicator);
        }
    }
    updateTrackedTargets() {
        if (this.trackedTargetVisuals.size === 0) {
            return;
        }
        const viewBounds = this.getViewBounds();
        const worldUnitsPerPixel = this.getWorldUnitsPerPixel();
        const markerScale = Math.max(10, worldUnitsPerPixel * 20);
        const edgeIndicatorScale = Math.max(11, worldUnitsPerPixel * 22);
        const sourceUnit = this.renderableUnitsById.get(this.selectedControllableId) ?? null;
        const sourceVisible = sourceUnit ? this.isUnitVisible(sourceUnit, viewBounds) : false;
        const rayAnchor = sourceVisible && sourceUnit
            ? { x: sourceUnit.x, y: sourceUnit.y }
            : { x: this.camera.position.x, y: -this.camera.position.y };
        for (const [unitId, visual] of this.trackedTargetVisuals.entries()) {
            const targetUnit = this.renderableUnitsById.get(unitId);
            if (!targetUnit) {
                this.hideTrackedTargetVisual(visual);
                continue;
            }
            const targetVisible = this.isUnitVisible(targetUnit, viewBounds);
            visual.marker.scale.set(markerScale, markerScale, 1);
            visual.edgeIndicator.scale.set(edgeIndicatorScale, edgeIndicatorScale, 1);
            if (targetVisible) {
                visual.marker.visible = true;
                visual.marker.position.set(targetUnit.x, -targetUnit.y, 8);
                visual.edgeIndicator.visible = false;
                if (sourceVisible && sourceUnit && sourceUnit.unitId !== targetUnit.unitId) {
                    const trackedLine = this.buildTrackedLinePoints(sourceUnit, targetUnit, worldUnitsPerPixel);
                    if (trackedLine) {
                        visual.line.geometry.setFromPoints([
                            new THREE.Vector3(trackedLine.startX, -trackedLine.startY, 7),
                            new THREE.Vector3(trackedLine.endX, -trackedLine.endY, 7),
                        ]);
                        visual.line.visible = true;
                    }
                    else {
                        visual.line.visible = false;
                    }
                }
                else {
                    visual.line.visible = false;
                }
                continue;
            }
            visual.marker.visible = false;
            visual.line.visible = false;
            const edgePoint = getViewEdgeIntersection(rayAnchor, { x: targetUnit.x, y: targetUnit.y }, viewBounds);
            if (!edgePoint) {
                visual.edgeIndicator.visible = false;
                continue;
            }
            visual.edgeIndicator.visible = true;
            visual.edgeIndicator.position.set(edgePoint.x, -edgePoint.y, 8);
        }
    }
    buildTrackedLinePoints(sourceUnit, targetUnit, worldUnitsPerPixel) {
        const deltaX = targetUnit.x - sourceUnit.x;
        const deltaY = targetUnit.y - sourceUnit.y;
        const distance = Math.hypot(deltaX, deltaY);
        if (distance <= 0.0001) {
            return null;
        }
        const unitX = deltaX / distance;
        const unitY = deltaY / distance;
        const startOffset = Math.max(sourceUnit.renderRadius * 1.08, worldUnitsPerPixel * 12);
        const endOffset = Math.max(targetUnit.renderRadius * 1.18, worldUnitsPerPixel * 12);
        if (distance <= startOffset + endOffset) {
            return null;
        }
        return {
            startX: sourceUnit.x + unitX * startOffset,
            startY: sourceUnit.y + unitY * startOffset,
            endX: targetUnit.x - unitX * endOffset,
            endY: targetUnit.y - unitY * endOffset,
        };
    }
    isSelectedControllableAlive() {
        if (!this.selectedControllableId) {
            return false;
        }
        const overlayState = this.ownerOverlay[this.selectedControllableId];
        const publicControllable = this.snapshot?.controllables.find((entry) => entry.controllableId === this.selectedControllableId);
        if (publicControllable) {
            return getControllableAliveState(publicControllable, overlayState);
        }
        if (overlayState && typeof overlayState === 'object' && Object.prototype.hasOwnProperty.call(overlayState, 'alive')) {
            return typeof overlayState.alive === 'boolean'
                ? overlayState.alive
                : true;
        }
        return true;
    }
    updateNavigationPathPreview() {
        const path = readNavigationPath(this.ownerOverlay, this.selectedControllableId);
        if (!path || path.length < 2) {
            this.navigationPathPreview.visible = false;
            this.setDynamicLinePoints(this.navigationPathPreview, []);
            return;
        }
        this.setDynamicLinePoints(this.navigationPathPreview, path.map((point) => new THREE.Vector3(point.x, -point.y, 3.2)));
        this.navigationPathPreview.renderOrder = 39;
        this.navigationPathPreview.frustumCulled = false;
        this.navigationPathPreview.visible = true;
    }
    updateTrajectoryPreview() {
        const trajectory = readTrajectory(this.ownerOverlay, this.selectedControllableId);
        if (!trajectory || trajectory.length < 2) {
            this.trajectoryPreview.visible = false;
            this.setDynamicLinePoints(this.trajectoryPreview, []);
            return;
        }
        this.setDynamicLinePoints(this.trajectoryPreview, trajectory.map((point) => new THREE.Vector3(point.x, -point.y, 3.3)));
        this.trajectoryPreview.renderOrder = 42;
        this.trajectoryPreview.frustumCulled = false;
        this.trajectoryPreview.visible = true;
    }
    updateUnseenTrajectoryPreview() {
        if (!this.snapshot) {
            this.unseenTrajectoryPreview.visible = false;
            this.setDynamicLinePoints(this.unseenTrajectoryPreview, []);
            return;
        }
        const points = [];
        for (const unit of this.snapshot.units) {
            if (unit.isStatic
                || unit.isSeen
                || !isPlayerShipUnitKind(unit.kind)
                || !Array.isArray(unit.predictedTrajectory)
                || unit.predictedTrajectory.length < 2) {
                continue;
            }
            appendDottedTrajectory(points, unit.predictedTrajectory, 3.08, 18, 6, 0.42);
        }
        if (points.length === 0) {
            this.unseenTrajectoryPreview.visible = false;
            this.setDynamicLinePoints(this.unseenTrajectoryPreview, []);
            return;
        }
        this.unseenTrajectoryPreview.renderOrder = 40;
        this.unseenTrajectoryPreview.frustumCulled = false;
        this.setDynamicLinePoints(this.unseenTrajectoryPreview, points);
        this.unseenTrajectoryPreview.visible = true;
    }
    updateNavigationSearchPreview() {
        const searchEdges = readNavigationSearch(this.ownerOverlay, this.selectedControllableId);
        if (!searchEdges || searchEdges.length === 0) {
            this.navigationSearchPreview.visible = false;
            this.setDynamicLinePoints(this.navigationSearchPreview, []);
            return;
        }
        const points = searchEdges.flatMap((segment) => [
            new THREE.Vector3(segment.startX, -segment.startY, 2.8),
            new THREE.Vector3(segment.endX, -segment.endY, 2.8),
        ]);
        this.navigationSearchPreview.renderOrder = 38;
        this.navigationSearchPreview.frustumCulled = false;
        this.setDynamicLinePoints(this.navigationSearchPreview, points);
        this.navigationSearchPreview.visible = true;
    }
    /**
     * Replaces line geometry. Always disposes the previous BufferGeometry so Three.js
     * never resizes an in-place position buffer (which triggers "Buffer size too small").
     */
    replaceLineGeometry(line, points) {
        const safe = this.sanitizePolylinePoints(line, points);
        const previous = line.geometry;
        previous.dispose();
        line.geometry = new THREE.BufferGeometry().setFromPoints(safe);
    }
    setDynamicLinePoints(line, points) {
        this.replaceLineGeometry(line, points);
    }
    /** THREE.Line needs ≥2 vertices; THREE.LineSegments needs an even count ≥2. Empty uses a degenerate segment. */
    sanitizePolylinePoints(line, points) {
        const degenerate = () => [new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, 0)];
        if (points.length === 0) {
            return degenerate();
        }
        if (line instanceof THREE.LineSegments) {
            let even = points.length % 2 === 0 ? points : points.slice(0, points.length - 1);
            if (even.length < 2) {
                even = degenerate();
            }
            return even;
        }
        if (points.length < 2) {
            return degenerate();
        }
        return points;
    }
    updateScannerCones() {
        const activeScanners = new Map();
        for (const [controllableId, overlay] of Object.entries(this.ownerOverlay)) {
            const scannerState = overlay.scanner;
            if (!scannerState || scannerState.active !== true)
                continue;
            const unit = this.renderableUnitsById.get(controllableId);
            if (!unit)
                continue;
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
            uniforms.color.value.set(teamColor);
            const scale = currentLength;
            cone.mesh.position.set(unit.x, -unit.y, 0);
            cone.mesh.rotation.set(0, 0, -currentAngle * (Math.PI / 180));
            cone.mesh.scale.set(scale, scale, 1);
        }
    }
    findNearestUnit(worldX, worldY) {
        const units = this.renderableUnits;
        if (units.length === 0) {
            return null;
        }
        let nearest = null;
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
    getUnitPickRadius(unit) {
        const minimumPickRadius = (MIN_PICK_RADIUS_PX + PICK_RADIUS_PADDING_PX) * this.getWorldUnitsPerPixel();
        return Math.max(unit.renderRadius, minimumPickRadius);
    }
    getViewBounds() {
        const visibleWorldWidth = (this.camera.right - this.camera.left) / this.camera.zoom;
        const visibleWorldHeight = (this.camera.top - this.camera.bottom) / this.camera.zoom;
        return {
            minX: this.camera.position.x - visibleWorldWidth / 2,
            maxX: this.camera.position.x + visibleWorldWidth / 2,
            minY: -this.camera.position.y - visibleWorldHeight / 2,
            maxY: -this.camera.position.y + visibleWorldHeight / 2,
        };
    }
    getWorldUnitsPerPixel() {
        const width = Math.max(this.container.clientWidth, 1);
        const height = Math.max(this.container.clientHeight, 1);
        const visibleWorldWidth = (this.camera.right - this.camera.left) / this.camera.zoom;
        const visibleWorldHeight = (this.camera.top - this.camera.bottom) / this.camera.zoom;
        return Math.max(visibleWorldWidth / width, visibleWorldHeight / height);
    }
    reportVisibleUnits() {
        if (!this.onVisibleUnitsChanged) {
            return;
        }
        const viewBounds = this.getViewBounds();
        const visibleUnitIds = this.renderableUnits
            .filter((unit) => this.isUnitVisible(unit, viewBounds))
            .map((unit) => unit.unitId)
            .sort();
        if (visibleUnitIds.length === this.lastReportedVisibleUnitIds.length
            && visibleUnitIds.every((unitId, index) => unitId === this.lastReportedVisibleUnitIds[index])) {
            return;
        }
        this.lastReportedVisibleUnitIds = visibleUnitIds;
        this.onVisibleUnitsChanged(visibleUnitIds);
    }
    updateGravityStrengthGrid() {
        const viewBounds = this.getViewBounds();
        const viewPrecision = this.gravityStrengthGrid.cellSize / 4;
        const viewSignature = [
            roundTo(viewBounds.minX, viewPrecision),
            roundTo(viewBounds.maxX, viewPrecision),
            roundTo(viewBounds.minY, viewPrecision),
            roundTo(viewBounds.maxY, viewPrecision),
            roundTo(this.camera.zoom, 0.001),
        ].join('|');
        const gravitySources = [];
        const sourceSignatureParts = [];
        for (const unit of this.renderableUnits) {
            if (unit.isTrace) {
                continue;
            }
            const gravity = numberValue(unit.gravity, 0);
            if (gravity <= 0) {
                continue;
            }
            gravitySources.push({
                x: unit.x,
                y: unit.y,
                gravity,
            });
            sourceSignatureParts.push(unit.unitId, `${roundTo(unit.x, 0.1)}`, `${roundTo(unit.y, 0.1)}`, `${roundTo(gravity, 0.0001)}`);
        }
        const sourceSignature = sourceSignatureParts.join('|');
        if (viewSignature === this.lastGravityGridViewSignature
            && sourceSignature === this.lastGravityGridSourceSignature) {
            return;
        }
        this.lastGravityGridViewSignature = viewSignature;
        this.lastGravityGridSourceSignature = sourceSignature;
        this.gravityStrengthGrid.update(viewBounds, gravitySources);
    }
    clientToWorld(clientX, clientY) {
        const rect = this.container.getBoundingClientRect();
        const ndc = new THREE.Vector3(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1, 0);
        ndc.unproject(this.camera);
        return { x: ndc.x, y: ndc.y };
    }
    disposeVisual(visual) {
        visual.heading?.geometry.dispose();
        visual.heading?.material?.dispose();
    }
    disposeVisuals() {
        for (const visual of this.unitVisuals.values()) {
            this.disposeVisual(visual);
        }
        this.unitVisuals.clear();
        for (const visual of this.shipStatusVisuals.values()) {
            this.root.remove(visual.group);
            disposeShipStatusVisual(visual);
        }
        this.shipStatusVisuals.clear();
        this.selectedRing.geometry.dispose();
        this.selectedRing.material.dispose();
        this.tacticalTargetRing.geometry.dispose();
        this.tacticalTargetRing.material.dispose();
        this.selectedCenterDot.geometry.dispose();
        this.selectedCenterDot.material.dispose();
        for (const child of this.navigationMarker.children) {
            if (child instanceof THREE.Line || child instanceof THREE.LineLoop || child instanceof THREE.LineSegments) {
                child.geometry.dispose();
                child.material.dispose();
            }
        }
        this.navigationPointer.geometry.dispose();
        this.navigationPointer.material.dispose();
        this.navigationPathPreview.geometry.dispose();
        this.navigationPathPreview.material.dispose();
        this.navigationSearchPreview.geometry.dispose();
        this.navigationSearchPreview.material.dispose();
        this.unseenTrajectoryPreview.geometry.dispose();
        this.unseenTrajectoryPreview.material.dispose();
        this.trajectoryPreview.geometry.dispose();
        this.trajectoryPreview.material.dispose();
    }
    disposeTrackedTargetVisuals() {
        for (const visual of this.trackedTargetVisuals.values()) {
            this.root.remove(visual.line);
            this.root.remove(visual.marker);
            this.root.remove(visual.edgeIndicator);
            disposeTrackedTargetVisual(visual);
        }
        this.trackedTargetVisuals.clear();
    }
    disposeBodyMeshes() {
        this.staticBodies.mesh.geometry.dispose();
        this.dynamicBodies.mesh.geometry.dispose();
    }
    disposeScannerCones() {
        for (const [, cone] of this.scannerCones) {
            this.root.remove(cone.mesh);
            cone.mesh.geometry.dispose();
            cone.mesh.material.dispose();
        }
        this.scannerCones.clear();
    }
    ensureBodyMeshCapacity(bodies, requiredCount) {
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
    syncBodyInstances(bodies, units) {
        const nextBodies = this.ensureBodyMeshCapacity(bodies, units.length);
        if (bodies === this.staticBodies) {
            this.staticBodies = nextBodies;
        }
        else if (bodies === this.dynamicBodies) {
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
    buildNormalizedUnits(units) {
        const normalizedUnits = [];
        for (const unit of units) {
            normalizedUnits.push(this.normalizeUnit(unit, false));
        }
        return normalizedUnits;
    }
    isUnitVisible(unit, viewBounds) {
        const padding = unit.renderRadius;
        return unit.x + padding >= viewBounds.minX
            && unit.x - padding <= viewBounds.maxX
            && unit.y + padding >= viewBounds.minY
            && unit.y - padding <= viewBounds.maxY;
    }
    hideTrackedTargetVisual(visual) {
        visual.marker.visible = false;
        visual.line.visible = false;
        visual.edgeIndicator.visible = false;
    }
    captureLostUnitTraces(visibleUnits) {
        const visibleUnitIds = new Set();
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
    pruneShortLivedUnitCache(liveUnitIds) {
        for (const [cacheKey, cached] of this.normalizedUnitsByKey.entries()) {
            if (liveUnitIds.has(cached.unit.unitId) || !isShortLivedTransientUnitKind(cached.unit.kind)) {
                continue;
            }
            this.normalizedUnitsByKey.delete(cacheKey);
        }
    }
    normalizeUnit(unit, isTrace) {
        const cacheKey = `${unit.unitId}:${isTrace ? 'trace' : 'base'}`;
        const signature = buildRenderableUnitSignature(unit, isTrace);
        const cached = this.normalizedUnitsByKey.get(cacheKey);
        if (cached && cached.signature === signature) {
            return cached.unit;
        }
        const renderKind = normalizeKind(unit.kind);
        const normalizedUnit = {
            unitId: unit.unitId,
            clusterId: unit.clusterId,
            kind: unit.kind,
            isStatic: unit.isStatic,
            isSolid: unit.isSolid,
            isSeen: unit.isSeen,
            lastSeenTick: unit.lastSeenTick,
            x: unit.x,
            y: unit.y,
            angle: unit.angle,
            radius: unit.radius,
            gravity: unit.gravity,
            teamName: unit.teamName,
            isTrace,
            renderKind,
            renderRadius: getRenderRadius(unit.radius, renderKind),
        };
        this.normalizedUnitsByKey.set(cacheKey, { signature, unit: normalizedUnit });
        return normalizedUnit;
    }
}
function normalizeWheelDelta(event) {
    switch (event.deltaMode) {
        case WheelEvent.DOM_DELTA_LINE:
            return event.deltaY * 16;
        case WheelEvent.DOM_DELTA_PAGE:
            return event.deltaY * 160;
        default:
            return event.deltaY;
    }
}
function isEditableTarget(target) {
    if (!(target instanceof HTMLElement)) {
        return false;
    }
    return target instanceof HTMLInputElement
        || target instanceof HTMLTextAreaElement
        || target instanceof HTMLSelectElement
        || target.isContentEditable;
}
export function formatTeamAccent(teamName, teams) {
    if (!teamName) {
        return '#d7ecff';
    }
    return teams.find((team) => team.name === teamName)?.colorHex ?? '#d7ecff';
}
export function readNavigationTarget(ownerOverlay, controllableId) {
    const navigationState = readNavigationOverlay(ownerOverlay, controllableId);
    if (!navigationState || navigationState.active !== true) {
        return null;
    }
    return {
        x: numberValue(navigationState.targetX, 0),
        y: numberValue(navigationState.targetY, 0),
    };
}
export function readNavigationPointer(ownerOverlay, controllableId) {
    const overlay = ownerOverlay[controllableId];
    if (!overlay || typeof overlay !== 'object') {
        return null;
    }
    const overlayState = overlay;
    const navigationState = readNavigationOverlay(ownerOverlay, controllableId);
    if (!navigationState || navigationState.active !== true) {
        return null;
    }
    const position = overlayState.position;
    const positionState = position && typeof position === 'object'
        ? position
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
export function readNavigationPath(ownerOverlay, controllableId) {
    const navigationState = readNavigationOverlay(ownerOverlay, controllableId);
    if (!navigationState || navigationState.active !== true || !Array.isArray(navigationState.path)) {
        return null;
    }
    return navigationState.path
        .map((value) => objectValue(value))
        .filter((value) => value !== undefined)
        .map((point) => ({
        x: numberValue(point.x, 0),
        y: numberValue(point.y, 0),
    }));
}
export function readNavigationSearch(ownerOverlay, controllableId) {
    const navigationState = readNavigationOverlay(ownerOverlay, controllableId);
    if (!navigationState || navigationState.active !== true || !Array.isArray(navigationState.searchEdges)) {
        return null;
    }
    return navigationState.searchEdges
        .map((value) => objectValue(value))
        .filter((value) => value !== undefined)
        .map((segment) => ({
        startX: numberValue(segment.startX, 0),
        startY: numberValue(segment.startY, 0),
        endX: numberValue(segment.endX, 0),
        endY: numberValue(segment.endY, 0),
    }));
}
export function readTrajectory(ownerOverlay, controllableId) {
    const navigationState = readNavigationOverlay(ownerOverlay, controllableId);
    if (!navigationState || navigationState.active !== true || !Array.isArray(navigationState.trajectory)) {
        return null;
    }
    return navigationState.trajectory
        .map((value) => objectValue(value))
        .filter((value) => value !== undefined)
        .map((point) => ({
        x: numberValue(point.x, 0),
        y: numberValue(point.y, 0),
    }));
}
function normalizeOwnerOverlay(ownerOverlay) {
    return ownerOverlay && typeof ownerOverlay === 'object'
        ? ownerOverlay
        : {};
}
function readNavigationOverlay(ownerOverlay, controllableId) {
    const overlay = ownerOverlay[controllableId];
    if (!overlay || typeof overlay !== 'object') {
        return null;
    }
    const navigation = overlay.navigation;
    if (!navigation || typeof navigation !== 'object') {
        return null;
    }
    return navigation;
}
function sameNavigationTarget(left, right) {
    if (left === right) {
        return true;
    }
    if (!left || !right) {
        return left === right;
    }
    return left.x === right.x && left.y === right.y;
}
function sameTrackedUnitColors(left, right) {
    if (left === right) {
        return true;
    }
    const leftEntries = Object.entries(left);
    const rightEntries = Object.entries(right);
    if (leftEntries.length !== rightEntries.length) {
        return false;
    }
    return leftEntries.every(([unitId, color]) => right[unitId] === color);
}
function sameColorAssignments(left, right) {
    if (left === right) {
        return true;
    }
    const leftEntries = Object.entries(left);
    const rightEntries = Object.entries(right);
    if (leftEntries.length !== rightEntries.length) {
        return false;
    }
    return leftEntries.every(([unitId, color]) => right[unitId] === color);
}
function sameRenderableUnits(left, right) {
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
        if (leftUnit.unitId !== rightUnit.unitId
            || leftUnit.clusterId !== rightUnit.clusterId
            || leftUnit.kind !== rightUnit.kind
            || leftUnit.isStatic !== rightUnit.isStatic
            || (leftUnit.isSolid ?? true) !== (rightUnit.isSolid ?? true)
            || leftUnit.isSeen !== rightUnit.isSeen
            || leftUnit.lastSeenTick !== rightUnit.lastSeenTick
            || leftUnit.x !== rightUnit.x
            || leftUnit.y !== rightUnit.y
            || leftUnit.angle !== rightUnit.angle
            || leftUnit.radius !== rightUnit.radius
            || (leftUnit.gravity ?? 0) !== (rightUnit.gravity ?? 0)
            || leftUnit.teamName !== rightUnit.teamName
            || leftUnit.isTrace !== rightUnit.isTrace
            || leftUnit.renderKind !== rightUnit.renderKind
            || leftUnit.renderRadius !== rightUnit.renderRadius) {
            return false;
        }
    }
    return true;
}
function buildRenderableUnitSignature(unit, isTrace) {
    return [
        unit.unitId,
        unit.clusterId,
        unit.kind,
        unit.isStatic ? 1 : 0,
        unit.isSolid === false ? 0 : 1,
        unit.isSeen ? 1 : 0,
        unit.lastSeenTick,
        unit.x,
        unit.y,
        unit.angle,
        unit.radius,
        unit.gravity ?? 0,
        unit.teamName ?? '',
        isTrace ? 1 : 0,
    ].join('|');
}
function getViewEdgeIntersection(anchor, target, viewBounds) {
    const deltaX = target.x - anchor.x;
    const deltaY = target.y - anchor.y;
    if (Math.abs(deltaX) <= 0.0001 && Math.abs(deltaY) <= 0.0001) {
        return null;
    }
    const intersectionFactors = [];
    if (deltaX > 0) {
        intersectionFactors.push((viewBounds.maxX - anchor.x) / deltaX);
    }
    else if (deltaX < 0) {
        intersectionFactors.push((viewBounds.minX - anchor.x) / deltaX);
    }
    if (deltaY > 0) {
        intersectionFactors.push((viewBounds.maxY - anchor.y) / deltaY);
    }
    else if (deltaY < 0) {
        intersectionFactors.push((viewBounds.minY - anchor.y) / deltaY);
    }
    const positiveFactor = intersectionFactors
        .filter((factor) => Number.isFinite(factor) && factor >= 0)
        .sort((left, right) => left - right)[0];
    if (positiveFactor === undefined) {
        return null;
    }
    return {
        x: THREE.MathUtils.clamp(anchor.x + deltaX * positiveFactor, viewBounds.minX, viewBounds.maxX),
        y: THREE.MathUtils.clamp(anchor.y + deltaY * positiveFactor, viewBounds.minY, viewBounds.maxY),
    };
}
function getControllableAliveState(publicControllable, overlayState) {
    if (overlayState && typeof overlayState === 'object' && Object.prototype.hasOwnProperty.call(overlayState, 'alive')) {
        return typeof overlayState.alive === 'boolean'
            ? overlayState.alive
            : publicControllable.alive;
    }
    return publicControllable.alive;
}
function numberValue(value, fallback) {
    return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}
function objectValue(value) {
    return value && typeof value === 'object'
        ? value
        : undefined;
}
function stringValue(value, fallback) {
    return typeof value === 'string' && value.length > 0 ? value : fallback;
}
function roundTo(value, precision) {
    if (!Number.isFinite(value) || !Number.isFinite(precision) || precision <= 0) {
        return value;
    }
    return Math.round(value / precision) * precision;
}
function appendDottedTrajectory(target, trajectory, z, dashLength, gapLength, lineHalfWidth = 0) {
    let draw = true;
    let remaining = dashLength;
    for (let index = 0; index < trajectory.length - 1; index++) {
        const start = trajectory[index];
        const end = trajectory[index + 1];
        let segmentLength = Math.hypot(end.x - start.x, end.y - start.y);
        if (segmentLength <= 0.0001) {
            continue;
        }
        const unitX = (end.x - start.x) / segmentLength;
        const unitY = (end.y - start.y) / segmentLength;
        let cursorX = start.x;
        let cursorY = start.y;
        while (segmentLength > 0.0001) {
            const step = Math.min(remaining, segmentLength);
            const nextX = cursorX + unitX * step;
            const nextY = cursorY + unitY * step;
            if (draw) {
                const offsetX = -unitY * lineHalfWidth;
                const offsetY = unitX * lineHalfWidth;
                appendLineSegment(target, cursorX, cursorY, nextX, nextY, z, 0, 0);
                if (lineHalfWidth > 0.0001) {
                    appendLineSegment(target, cursorX, cursorY, nextX, nextY, z, offsetX, offsetY);
                    appendLineSegment(target, cursorX, cursorY, nextX, nextY, z, -offsetX, -offsetY);
                }
            }
            cursorX = nextX;
            cursorY = nextY;
            segmentLength -= step;
            remaining -= step;
            if (remaining <= 0.0001) {
                draw = !draw;
                remaining = draw ? dashLength : gapLength;
            }
        }
    }
}
function appendLineSegment(target, startX, startY, endX, endY, z, offsetX, offsetY) {
    target.push(new THREE.Vector3(startX + offsetX, -(startY + offsetY), z), new THREE.Vector3(endX + offsetX, -(endY + offsetY), z));
}
