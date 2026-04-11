import * as THREE from 'three';
const HULL_FILL_COLOR = 0xffb48a;
const SHIELD_FILL_COLOR = 0x8dd1ff;
const FRIENDLY_TRACK_COLOR = 0x83f0b2;
const ENEMY_TRACK_COLOR = 0xff8e86;
const ARC_START = Math.PI * 0.95;
const ARC_END = Math.PI * 0.05;
export function createShipStatusVisual() {
    const group = new THREE.Group();
    group.frustumCulled = false;
    group.renderOrder = 41;
    group.visible = false;
    const hullTrack = createArcLine(FRIENDLY_TRACK_COLOR, 0.34);
    const hullValue = createArcLine(HULL_FILL_COLOR, 0.96);
    const shieldTrack = createArcLine(FRIENDLY_TRACK_COLOR, 0.28);
    const shieldValue = createArcLine(SHIELD_FILL_COLOR, 0.92);
    group.add(hullTrack);
    group.add(hullValue);
    group.add(shieldTrack);
    group.add(shieldValue);
    return {
        group,
        hullTrack,
        hullValue,
        shieldTrack,
        shieldValue,
    };
}
export function updateShipStatusVisual(visual, update) {
    const trackColor = update.relation === 'friendly' ? FRIENDLY_TRACK_COLOR : ENEMY_TRACK_COLOR;
    const outerRadius = Math.max(update.radius * 1.48, 6.5);
    const innerRadius = outerRadius - Math.max(update.radius * 0.26, 1.15);
    visual.group.visible = true;
    visual.group.position.set(update.x, update.y, 0);
    visual.hullTrack.material.color.setHex(trackColor);
    visual.shieldTrack.material.color.setHex(trackColor);
    setArcGeometry(visual.hullTrack, outerRadius, 1);
    setArcGeometry(visual.hullValue, outerRadius, update.hullRatio);
    setArcGeometry(visual.shieldTrack, innerRadius, 1);
    setArcGeometry(visual.shieldValue, innerRadius, update.shieldRatio);
}
export function hideShipStatusVisual(visual) {
    visual.group.visible = false;
}
export function disposeShipStatusVisual(visual) {
    for (const child of visual.group.children) {
        if (child instanceof THREE.Line) {
            child.geometry.dispose();
            child.material.dispose();
        }
    }
}
function createArcLine(color, opacity) {
    const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(createArcPoints(1, 1)), new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity,
        depthTest: false,
        depthWrite: false,
    }));
    line.frustumCulled = false;
    line.renderOrder = 41;
    return line;
}
function setArcGeometry(line, radius, ratio) {
    const previous = line.geometry;
    line.geometry = new THREE.BufferGeometry().setFromPoints(createArcPoints(radius, ratio));
    previous.dispose();
}
function createArcPoints(radius, ratio) {
    const clampedRatio = THREE.MathUtils.clamp(ratio, 0, 1);
    const endAngle = ARC_START + (ARC_END - ARC_START) * clampedRatio;
    const segmentCount = Math.max(2, Math.ceil(26 * clampedRatio));
    const points = [];
    for (let index = 0; index <= segmentCount; index++) {
        const t = segmentCount === 0 ? 0 : index / segmentCount;
        const angle = ARC_START + (endAngle - ARC_START) * t;
        points.push(new THREE.Vector3(Math.cos(angle) * radius, Math.sin(angle) * radius, 0));
    }
    if (points.length < 2) {
        points.push(points[0]?.clone() ?? new THREE.Vector3(radius, 0, 0));
    }
    return points;
}
