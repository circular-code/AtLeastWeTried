import * as THREE from 'three';
import { UNIT_BODY_VERTEX_SHADER, UNIT_BODY_FRAGMENT_SHADER } from './shaders/unitBody';
import { DEBUG_SUN_COUNT, DEBUG_SUN_BOUNDS_X, DEBUG_SUN_BOUNDS_Y } from './constants';
export function createUnitBodyMaterial() {
    return new THREE.ShaderMaterial({
        uniforms: {
            time: { value: 0 },
        },
        vertexShader: UNIT_BODY_VERTEX_SHADER,
        fragmentShader: UNIT_BODY_FRAGMENT_SHADER,
        blending: THREE.AdditiveBlending,
        transparent: true,
        depthWrite: false,
    });
}
export function createUnitBodyMesh(instanceCapacity, material) {
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
    const mesh = new THREE.InstancedMesh(geometry, material, instanceCapacity);
    mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    mesh.frustumCulled = false;
    return {
        mesh,
        colorAttribute: geometry.getAttribute('instanceBodyColor'),
        kindAttribute: geometry.getAttribute('instanceKind'),
        opacityAttribute: geometry.getAttribute('instanceOpacity'),
    };
}
export function createDebugSuns() {
    const debugUnits = [];
    for (let index = 0; index < DEBUG_SUN_COUNT; index++) {
        const radius = 22;
        debugUnits.push({
            unitId: `debug-sun-${index}`,
            clusterId: -1,
            kind: 'sun',
            isStatic: true,
            isSeen: true,
            lastSeenTick: 0,
            x: randomInRange(-DEBUG_SUN_BOUNDS_X, DEBUG_SUN_BOUNDS_X),
            y: randomInRange(-DEBUG_SUN_BOUNDS_Y, DEBUG_SUN_BOUNDS_Y),
            angle: randomInRange(0, 360),
            radius,
            teamName: undefined,
            isTrace: false,
            renderKind: 'sun',
            renderRadius: radius,
        });
    }
    return debugUnits;
}
function randomInRange(min, max) {
    return min + Math.random() * (max - min);
}
