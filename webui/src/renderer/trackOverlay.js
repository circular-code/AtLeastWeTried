import * as THREE from 'three';
import { createRingPoints } from './selectionRing';
export function createTrackedTargetVisual(color) {
    const marker = new THREE.Group();
    const markerRingMaterial = new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity: 0.96,
        depthTest: false,
        depthWrite: false,
    });
    const markerCrossMaterial = new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity: 0.9,
        depthTest: false,
        depthWrite: false,
    });
    marker.add(new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(createRingPoints(20, 1)), markerRingMaterial));
    marker.add(new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(-1.4, 0, 0),
        new THREE.Vector3(-0.46, 0, 0),
        new THREE.Vector3(0.46, 0, 0),
        new THREE.Vector3(1.4, 0, 0),
        new THREE.Vector3(0, -1.4, 0),
        new THREE.Vector3(0, -0.46, 0),
        new THREE.Vector3(0, 0.46, 0),
        new THREE.Vector3(0, 1.4, 0),
    ]), markerCrossMaterial));
    marker.frustumCulled = false;
    marker.renderOrder = 44;
    marker.visible = false;
    const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(0, 0, 0),
        new THREE.Vector3(0, 0, 0),
    ]), new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity: 0.9,
        depthTest: false,
        depthWrite: false,
    }));
    line.frustumCulled = false;
    line.renderOrder = 43;
    line.visible = false;
    const edgeIndicator = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(createRingPoints(20, 1)), new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity: 0.98,
        depthTest: false,
        depthWrite: false,
    }));
    edgeIndicator.frustumCulled = false;
    edgeIndicator.renderOrder = 44;
    edgeIndicator.visible = false;
    return {
        color,
        marker,
        line,
        edgeIndicator,
    };
}
export function disposeTrackedTargetVisual(visual) {
    for (const child of visual.marker.children) {
        if (child instanceof THREE.Line || child instanceof THREE.LineLoop || child instanceof THREE.LineSegments) {
            child.geometry.dispose();
            child.material.dispose();
        }
    }
    visual.line.geometry.dispose();
    visual.line.material.dispose();
    visual.edgeIndicator.geometry.dispose();
    visual.edgeIndicator.material.dispose();
}
