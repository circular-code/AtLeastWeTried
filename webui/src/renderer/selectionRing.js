import * as THREE from 'three';
export function createRingPoints(segmentCount, radius) {
    const points = [];
    for (let index = 0; index < segmentCount; index++) {
        const angle = (index / segmentCount) * Math.PI * 2;
        points.push(new THREE.Vector3(Math.cos(angle) * radius, Math.sin(angle) * radius, 0));
    }
    return points;
}
export function createSelectionRing() {
    const ring = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(createRingPoints(28, 1.25)), new THREE.LineBasicMaterial({ color: 0x9ff8ff, transparent: true, opacity: 0.95 }));
    ring.visible = false;
    return ring;
}
