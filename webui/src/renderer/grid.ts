import * as THREE from 'three';

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

export function createGlowField(): THREE.Group {
  const stars = new THREE.Group();
  const geometry = new THREE.CircleGeometry(1, 10);
  const material = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.12 });

  for (let index = 0; index < 140; index++) {
    const star = new THREE.Mesh(geometry, material.clone());
    star.position.set((Math.random() - 0.5) * 4200, (Math.random() - 0.5) * 2400, -100);
    const scale = 1 + Math.random() * 2.2;
    star.scale.set(scale, scale, 1);
    (star.material as THREE.MeshBasicMaterial).opacity = 0.05 + Math.random() * 0.18;
    stars.add(star);
  }

  return stars;
}
