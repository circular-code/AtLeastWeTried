import * as THREE from 'three';

export function createNavigationLookaheadMarker(): THREE.Group {
  const marker = new THREE.Group();
  const material = new THREE.LineBasicMaterial({
    color: 0x6be0ff,
    transparent: true,
    opacity: 0.95,
    depthTest: false,
    depthWrite: false,
  });

  marker.add(new THREE.LineLoop(
    new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(0, -1.35, 0),
      new THREE.Vector3(1.35, 0, 0),
      new THREE.Vector3(0, 1.35, 0),
      new THREE.Vector3(-1.35, 0, 0),
    ]),
    material,
  ));

  marker.add(new THREE.LineSegments(
    new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(-0.7, 0, 0),
      new THREE.Vector3(0.7, 0, 0),
      new THREE.Vector3(0, -0.7, 0),
      new THREE.Vector3(0, 0.7, 0),
    ]),
    material.clone(),
  ));

  marker.frustumCulled = false;
  marker.renderOrder = 41;
  marker.visible = false;
  return marker;
}
