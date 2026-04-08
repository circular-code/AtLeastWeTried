import * as THREE from 'three'

export function createObjectMaterial() {
  const material = new THREE.MeshBasicMaterial({
    color: 0xffffff,
  })

  material.onBeforeCompile = (shader) => {
    shader.vertexShader = shader.vertexShader
      .replace(
        '#include <common>',
        `
          #include <common>
          varying vec2 vCircleUv;
        `,
      )
      .replace(
        '#include <begin_vertex>',
        `
          #include <begin_vertex>
          vCircleUv = uv;
        `,
      )

    shader.fragmentShader = shader.fragmentShader
      .replace(
        '#include <common>',
        `
          #include <common>
          varying vec2 vCircleUv;
        `,
      )
      .replace(
        'vec4 diffuseColor = vec4( diffuse, opacity );',
        `
          vec2 localUv = vCircleUv * 2.0 - 1.0;
          float distanceFromCenter = length(localUv);

          if (distanceFromCenter > 1.0) {
            discard;
          }

          vec4 diffuseColor = vec4( diffuse, opacity );
        `,
      )
  }

  return material
}
