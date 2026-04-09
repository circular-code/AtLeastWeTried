import * as THREE from 'three';
import { SCANNER_CONE_VERTEX_SHADER, SCANNER_CONE_FRAGMENT_SHADER } from './shaders/scannerCone';

export type ScannerConeVisual = {
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>;
  controllableId: string;
};

export function createScannerConeMesh(
  controllableId: string,
  halfWidthRad: number,
  coneLength: number,
  targetHalfWidthRad: number,
  normalizedTargetLength: number,
  teamColor: string,
): ScannerConeVisual {
  const geo = new THREE.PlaneGeometry(2, 2);
  const mat = new THREE.ShaderMaterial({
    uniforms: {
      time: { value: 0 },
      halfWidthRad: { value: halfWidthRad },
      coneLength: { value: coneLength },
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
  return { mesh, controllableId };
}
