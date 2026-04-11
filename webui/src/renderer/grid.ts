import * as THREE from 'three';

const GRAVITY_GRID_CELL_SIZE = 120;
const GRAVITY_GRID_CELL_SCALE = 0.86;
const GRAVITY_GRID_PADDING_CELLS = 2;
const GRAVITY_GRID_INITIAL_CAPACITY = 512;
const GRAVITY_GRID_SPEED_LIMIT = 3.4;
const GRAVITY_GRID_STRENGTH_GAIN = 240;

export type GravityGridViewBounds = {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
};

export type GravitySource = {
  x: number;
  y: number;
  gravity: number;
};

export type GravityStrengthGrid = {
  group: THREE.Group;
  cellSize: number;
  update: (viewBounds: GravityGridViewBounds, sources: GravitySource[]) => void;
  dispose: () => void;
};

export function createGrid(): THREE.Group {
  const grid = new THREE.Group();
  const minorMaterial = new THREE.LineBasicMaterial({ color: 0x153042, transparent: true, opacity: 0.55 });
  const majorMaterial = new THREE.LineBasicMaterial({ color: 0x2b6d80, transparent: true, opacity: 0.42 });

  for (let index = -20; index <= 20; index++) {
    const coordinate = index * 120;
    const material = index % 5 === 0 ? majorMaterial : minorMaterial;

    grid.add(new THREE.Line(
      new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(-2400, coordinate, -20), new THREE.Vector3(2400, coordinate, -20)]),
      material,
    ));
    grid.add(new THREE.Line(
      new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(coordinate, -2400, -20), new THREE.Vector3(coordinate, 2400, -20)]),
      material,
    ));
  }

  return grid;
}

export function createGravityStrengthGrid(): GravityStrengthGrid {
  const group = new THREE.Group();
  const material = new THREE.MeshBasicMaterial({
    color: 0xffffff,
    transparent: true,
    opacity: 0.78,
    depthTest: true,
    depthWrite: false,
    blending: THREE.NormalBlending,
    vertexColors: true,
  });
  const tempTransform = new THREE.Object3D();
  const tempColor = new THREE.Color();
  const cellScale = GRAVITY_GRID_CELL_SIZE * GRAVITY_GRID_CELL_SCALE;
  let mesh = buildGravityMesh(material, GRAVITY_GRID_INITIAL_CAPACITY);

  group.add(mesh);

  return {
    group,
    cellSize: GRAVITY_GRID_CELL_SIZE,
    update(viewBounds, sources) {
      const minX = viewBounds.minX - GRAVITY_GRID_CELL_SIZE * GRAVITY_GRID_PADDING_CELLS;
      const maxX = viewBounds.maxX + GRAVITY_GRID_CELL_SIZE * GRAVITY_GRID_PADDING_CELLS;
      const minY = viewBounds.minY - GRAVITY_GRID_CELL_SIZE * GRAVITY_GRID_PADDING_CELLS;
      const maxY = viewBounds.maxY + GRAVITY_GRID_CELL_SIZE * GRAVITY_GRID_PADDING_CELLS;
      const startX = Math.floor(minX / GRAVITY_GRID_CELL_SIZE) * GRAVITY_GRID_CELL_SIZE + GRAVITY_GRID_CELL_SIZE / 2;
      const startY = Math.floor(minY / GRAVITY_GRID_CELL_SIZE) * GRAVITY_GRID_CELL_SIZE + GRAVITY_GRID_CELL_SIZE / 2;
      const endX = Math.ceil(maxX / GRAVITY_GRID_CELL_SIZE) * GRAVITY_GRID_CELL_SIZE - GRAVITY_GRID_CELL_SIZE / 2;
      const endY = Math.ceil(maxY / GRAVITY_GRID_CELL_SIZE) * GRAVITY_GRID_CELL_SIZE - GRAVITY_GRID_CELL_SIZE / 2;
      const xCount = Math.max(Math.floor((endX - startX) / GRAVITY_GRID_CELL_SIZE) + 1, 0);
      const yCount = Math.max(Math.floor((endY - startY) / GRAVITY_GRID_CELL_SIZE) + 1, 0);
      const sampleCount = xCount * yCount;

      if (sampleCount > mesh.instanceMatrix.count) {
        group.remove(mesh);
        mesh.geometry.dispose();
        mesh = buildGravityMesh(material, sampleCount);
        group.add(mesh);
      }

      let visibleCount = 0;

      for (let x = startX; x <= endX; x += GRAVITY_GRID_CELL_SIZE) {
        for (let y = startY; y <= endY; y += GRAVITY_GRID_CELL_SIZE) {
          const strength = computeGravityStrengthAtPoint(x, y, sources);
          const normalizedStrength = 1 - Math.exp(-strength * GRAVITY_GRID_STRENGTH_GAIN);
          tempTransform.position.set(x, -y, -19);
          tempTransform.scale.set(cellScale, cellScale, 1);
          tempTransform.rotation.set(0, 0, 0);
          tempTransform.updateMatrix();
          mesh.setMatrixAt(visibleCount, tempTransform.matrix);
          mesh.setColorAt(visibleCount, getStrengthColor(normalizedStrength, tempColor));
          visibleCount += 1;
        }
      }

      mesh.count = visibleCount;
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) {
        mesh.instanceColor.needsUpdate = true;
      }
    },
    dispose() {
      group.remove(mesh);
      mesh.geometry.dispose();
      material.dispose();
    },
  };
}

export function createGlowField(): THREE.Group {
  const stars = new THREE.Group();
  const geometry = new THREE.CircleGeometry(1, 10);
  const material = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.12 });
  const starCount = 140;
  const mesh = new THREE.InstancedMesh(geometry, material, starCount);
  mesh.frustumCulled = false;

  const tempTransform = new THREE.Object3D();
  const tempColor = new THREE.Color();

  for (let index = 0; index < starCount; index++) {
    const scale = 1 + Math.random() * 2.2;
    const opacity = 0.05 + Math.random() * 0.18;

    tempTransform.position.set((Math.random() - 0.5) * 4200, (Math.random() - 0.5) * 2400, -100);
    tempTransform.scale.set(scale, scale, 1);
    tempTransform.rotation.set(0, 0, 0);
    tempTransform.updateMatrix();
    mesh.setMatrixAt(index, tempTransform.matrix);
    // Encode opacity as color brightness since InstancedMesh shares one material
    tempColor.setRGB(opacity / 0.18, opacity / 0.18, opacity / 0.18);
    mesh.setColorAt(index, tempColor);
  }

  mesh.instanceMatrix.needsUpdate = true;
  if (mesh.instanceColor) {
    mesh.instanceColor.needsUpdate = true;
  }

  stars.add(mesh);
  return stars;
}

function buildGravityMesh(material: THREE.MeshBasicMaterial, capacity: number) {
  const mesh = new THREE.InstancedMesh(new THREE.PlaneGeometry(1, 1), material, capacity);
  mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
  mesh.frustumCulled = false;
  mesh.renderOrder = -4;
  return mesh;
}

function computeGravityStrengthAtPoint(targetX: number, targetY: number, sources: GravitySource[]) {
  let speedX = 0;
  let speedY = 0;

  for (const source of sources) {
    if (source.gravity <= 0 || !Number.isFinite(source.gravity)) {
      continue;
    }

    const dx = source.x - targetX;
    const dy = source.y - targetY;
    const d2 = dx * dx + dy * dy;

    if (d2 > 3600) {
      const factor = source.gravity * 60 / d2;
      speedX += dx * factor;
      speedY += dy * factor;
      continue;
    }

    if (d2 > 0) {
      const invDistance = 1 / Math.sqrt(d2);
      speedX += dx * invDistance * source.gravity;
      speedY += dy * invDistance * source.gravity;
      continue;
    }

    speedX += source.gravity;
  }

  let speed = Math.hypot(speedX, speedY);
  if (speed > GRAVITY_GRID_SPEED_LIMIT && speed > 0) {
    const clampedSpeed = GRAVITY_GRID_SPEED_LIMIT + 0.9 * (speed - GRAVITY_GRID_SPEED_LIMIT);
    const speedScale = clampedSpeed / speed;
    speedX *= speedScale;
    speedY *= speedScale;
    speed = Math.hypot(speedX, speedY);
  }

  return speed;
}

function getStrengthColor(normalizedStrength: number, target: THREE.Color) {
  const clamped = THREE.MathUtils.clamp(normalizedStrength, 0, 1);
  const hue = THREE.MathUtils.lerp(0.58, 0.05, clamped);
  const saturation = THREE.MathUtils.lerp(0.42, 0.95, clamped);
  const lightness = THREE.MathUtils.lerp(0.2, 0.68, clamped);
  return target.setHSL(hue, saturation, lightness);
}
