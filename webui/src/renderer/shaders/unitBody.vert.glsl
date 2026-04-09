attribute vec3 instanceBodyColor;
attribute float instanceKind;
attribute float instanceOpacity;

varying vec2 vLocalUv;
varying float vKind;
varying float vOpacity;
varying vec3 vColor;

void main() {
  vLocalUv = uv * 2.0 - 1.0;
  vKind = instanceKind;
  vOpacity = instanceOpacity;
  vColor = instanceBodyColor;
  gl_Position = projectionMatrix * modelViewMatrix * instanceMatrix * vec4(position, 1.0);
}
