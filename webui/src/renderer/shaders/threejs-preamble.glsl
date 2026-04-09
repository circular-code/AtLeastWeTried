// Three.js ShaderMaterial built-in uniforms & attributes
// Declared here so the shader validator can resolve them without errors.

// Three.js injects these precision qualifiers before all shader sources
precision highp float;
precision highp int;
precision highp sampler2D;
precision highp samplerCube;

uniform mat4 modelMatrix;
uniform mat4 modelViewMatrix;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat3 normalMatrix;
uniform vec3 cameraPosition;

attribute vec3 position;
attribute vec3 normal;
attribute vec2 uv;
attribute vec2 uv2;

// InstancedMesh built-ins
attribute mat4 instanceMatrix;
attribute vec3 instanceColor;
