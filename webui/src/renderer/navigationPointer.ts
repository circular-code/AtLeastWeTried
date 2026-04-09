import * as THREE from 'three';

export function createNavigationPointer(): THREE.LineSegments {
  const pointer = new THREE.LineSegments(
    new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(0, 0, 0),
    ]),
    new THREE.LineBasicMaterial({
      color: 0xffd36b,
      transparent: true,
      opacity: 0.94,
      depthTest: false,
      depthWrite: false,
    }),
  );

  pointer.frustumCulled = false;
  pointer.renderOrder = 40;
  pointer.visible = false;
  return pointer;
}
