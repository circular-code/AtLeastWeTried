import * as THREE from 'three';
import { createRingPoints } from './selectionRing';

export function createNavigationMarker(): THREE.Group {
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
