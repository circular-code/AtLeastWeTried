varying vec2 vLocalUv;

void main() {
  vLocalUv = uv * 2.0 - 1.0;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}
