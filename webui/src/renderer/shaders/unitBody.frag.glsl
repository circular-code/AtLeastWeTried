uniform float time;

varying vec2 vLocalUv;
varying float vKind;
varying float vOpacity;
varying vec3 vColor;

const float PI = 3.14159265359;

float maskCircle(vec2 point, float radius) {
  return 1.0 - smoothstep(radius, radius + 0.035, length(point));
}

float maskEllipse(vec2 point, vec2 radii) {
  return 1.0 - smoothstep(1.0, 1.05, length(point / radii));
}

float maskRoundedBox(vec2 point, vec2 halfSize, float radius) {
  vec2 distance = abs(point) - halfSize + vec2(radius);
  float signedDistance = length(max(distance, 0.0)) + min(max(distance.x, distance.y), 0.0) - radius;
  return 1.0 - smoothstep(0.0, 0.04, signedDistance);
}

float maskRing(vec2 point, float innerRadius, float outerRadius) {
  float distance = length(point);
  float outer = 1.0 - smoothstep(outerRadius, outerRadius + 0.03, distance);
  float inner = smoothstep(innerRadius - 0.03, innerRadius + 0.03, distance);
  return outer * inner;
}

float maskDiamond(vec2 point, vec2 halfSize) {
  float distance = abs(point.x / halfSize.x) + abs(point.y / halfSize.y);
  return 1.0 - smoothstep(1.0, 1.08, distance);
}

float hash21(vec2 point) {
  point = fract(point * vec2(123.34, 456.21));
  point += dot(point, point + 45.32);
  return fract(point.x * point.y);
}

float stripeField(vec2 point, float frequency, float warp) {
  float band = sin(point.y * frequency + sin(point.x * warp) * 1.6 + point.x * 0.9);
  return 0.5 + 0.5 * band;
}

float pulse(float speed, float phase) {
  return 0.5 + 0.5 * sin(time * speed + phase);
}

float craterField(vec2 point) {
  vec2 shifted = point * 3.6;
  float craterA = 1.0 - smoothstep(0.18, 0.3, length(fract(shifted + vec2(0.12, 0.31)) - 0.5));
  float craterB = 1.0 - smoothstep(0.14, 0.26, length(fract(shifted * 0.82 + vec2(0.67, 0.18)) - 0.5));
  float craterC = 1.0 - smoothstep(0.16, 0.29, length(fract(shifted * 1.17 + vec2(0.41, 0.77)) - 0.5));
  return max(max(craterA, craterB), craterC);
}

vec3 applyPlanetLighting(vec2 point, vec3 baseColor, float surfaceMask, float atmosphereMask, float surfaceNoise) {
  vec2 lightDir = normalize(vec2(-0.7, 0.45));
  float normalZ = sqrt(max(0.0, 1.0 - dot(point, point)));
  vec3 normal = normalize(vec3(point * 0.92, normalZ));
  vec3 light = normalize(vec3(lightDir, 0.75));
  float diffuse = clamp(dot(normal, light), 0.0, 1.0);
  float fresnel = pow(1.0 - max(normal.z, 0.0), 2.4);
  vec3 lit = baseColor * (0.42 + diffuse * 0.9);
  lit += vec3(0.55, 0.76, 1.0) * atmosphereMask * (0.15 + fresnel * 0.5);
  lit += vec3(1.0) * surfaceNoise * surfaceMask * 0.07;
  return lit;
}

float maskShip(vec2 point) {
  return maskCircle(point, 0.7);
}

float maskShipDirection(vec2 point) {
  vec2 markerPoint = point - vec2(0.02, 0.0);
  float shaft = maskRoundedBox(markerPoint + vec2(0.12, 0.0), vec2(0.22, 0.055), 0.035);
  float head = maskDiamond(markerPoint - vec2(0.22, 0.0), vec2(0.18, 0.14));
  return max(shaft, head);
}

void main() {
  vec2 point = vLocalUv;
  vec2 animatedPoint = point;
  float baseMask = 0.0;
  float glowMask = 0.0;
  float kind = vKind;
  vec3 color = vColor;
  float emissiveBoost = 1.0;
  vec3 detailColor = vec3(0.0);

  if (kind < 0.5) {
    float prismPulse = pulse(2.2, point.x * 2.4 + point.y * 1.8);
    baseMask = maskDiamond(point, vec2(0.84 + prismPulse * 0.1, 0.84 + prismPulse * 0.1));
    glowMask = maskDiamond(point, vec2(1.0, 1.0)) * (0.08 + prismPulse * 0.08);
  } else if (kind < 1.5) {
    float distance = length(point);
    float angle = atan(point.y, point.x);
    float turbulence =
      sin(angle * 6.0 + distance * 11.0 - time * 2.8) * 0.5 +
      sin(angle * 11.0 - distance * 17.0 + time * 4.1) * 0.3 +
      cos(point.x * 12.0 - point.y * 9.0 + time * 3.2) * 0.2;
    float flare = 0.5 + 0.5 * turbulence;
    float coreMask = maskCircle(point, 0.44);
    float plasmaMask = maskCircle(point, 0.62 + (flare - 0.5) * 0.1);
    float coronaMask = 1.0 - smoothstep(0.68 + (flare - 0.5) * 0.18, 1.02, distance);
    float emberBand = smoothstep(0.52, 0.95, flare) * (1.0 - smoothstep(0.2, 0.72, distance));

    baseMask = max(plasmaMask, coreMask);
    glowMask = coronaMask * 0.58 + emberBand * 0.28;

    vec3 emberColor = vec3(1.0, 0.32, 0.06);
    vec3 flameColor = vec3(1.0, 0.62, 0.14);
    vec3 coreColor = vec3(1.0, 0.95, 0.74);
    color = mix(emberColor, flameColor, smoothstep(0.18, 0.72, 1.0 - distance));
    color = mix(color, coreColor, coreMask * 0.92 + emberBand * 0.24);
    emissiveBoost = 1.16 + flare * 0.24;
  } else if (kind < 4.5) {
    float distance = length(point);
    float surfaceMask = maskCircle(point, 0.7);
    float atmosphereMask = maskCircle(point, 0.82) - surfaceMask * 0.72;
    float rimMask = smoothstep(0.32, 0.92, distance) * surfaceMask;
    baseMask = surfaceMask;
    glowMask = maskCircle(point, 0.95) * 0.12 + atmosphereMask * 0.55;

    if (kind < 2.5) {
      animatedPoint.x += time * 0.045;
      float bands = stripeField(animatedPoint, 13.0, 6.5);
      float storms = 0.5 + 0.5 * sin(animatedPoint.x * 9.0 - animatedPoint.y * 4.0 + bands * 4.5 + time * 1.4);
      float surfaceNoise = bands * 0.7 + storms * 0.3;
      vec3 oceanColor = mix(color * 0.72, color * 1.16 + vec3(0.08, 0.14, 0.18), surfaceNoise);
      vec3 cloudColor = vec3(0.92, 0.98, 1.0);
      color = applyPlanetLighting(point, oceanColor, surfaceMask, atmosphereMask, surfaceNoise);
      detailColor += cloudColor * smoothstep(0.6, 0.88, surfaceNoise) * rimMask * 0.24;
    } else if (kind < 3.5) {
      float craterNoise = craterField(point + vec2(0.08, -0.03) + vec2(sin(time * 0.18), cos(time * 0.18)) * 0.015);
      float surfaceNoise = 0.35 + hash21(floor((point + 1.0) * 6.0 + vec2(time * 0.12))) * 0.65;
      vec3 moonBase = mix(color * 0.7, vec3(0.88, 0.9, 0.96), surfaceNoise * 0.36);
      color = applyPlanetLighting(point, moonBase, surfaceMask, atmosphereMask * 0.45, surfaceNoise * 0.4);
      detailColor -= vec3(0.1, 0.1, 0.12) * craterNoise * surfaceMask * 0.45;
    } else {
      float rockNoise = 0.5 + 0.5 * sin(point.x * 10.0 + point.y * 8.0 + sin(point.x * 4.0 + time * 0.8) + time * 0.55);
      vec3 rockColor = mix(color * 0.72, color * 1.18 + vec3(0.08, 0.06, 0.03), rockNoise);
      color = applyPlanetLighting(point, rockColor, surfaceMask, atmosphereMask * 0.25, rockNoise * 0.5);
      detailColor += vec3(1.0, 0.74, 0.45) * rimMask * 0.06;
    }
  } else if (kind < 6.5) {
    baseMask = maskRing(point, 0.42, 0.78);
    float swirl = sin(atan(point.y, point.x) * 5.0 - time * 2.2) * 0.5 + 0.5;
    glowMask = maskCircle(point, 1.05) * (0.12 + swirl * 0.18);
    detailColor += vec3(0.35, 0.9, 1.0) * baseMask * swirl * 0.3;
  } else if (kind < 9.5) {
    float hullMask = maskShip(point);
    float coreMask = maskCircle(point, 0.52);
    float directionMask = maskShipDirection(point) * coreMask;
    float innerShadow = smoothstep(0.12, 0.74, length(point)) * hullMask;
    float rimMask = hullMask - coreMask * 0.92;
    float enginePulse = pulse(6.5, point.x * 4.0);
    float forwardGlow = maskEllipse(point - vec2(0.18 + enginePulse * 0.04, 0.0), vec2(0.24, 0.18)) * (0.22 + enginePulse * 0.18);
    vec3 hullColor = mix(color * 0.68, color * 1.08 + vec3(0.08, 0.1, 0.14), smoothstep(-0.75, 0.45, point.x));

    baseMask = hullMask;
    glowMask = maskCircle(point, 0.96) * 0.12 + forwardGlow;
    color = hullColor * (0.82 + (1.0 - innerShadow) * 0.28);
    color *= 1.0 - directionMask * 0.92;
    detailColor += vec3(1.0) * rimMask * 0.16;
    detailColor += vec3(0.6, 0.9, 1.0) * forwardGlow * 0.42;
  } else if (kind < 10.5) {
    baseMask = maskEllipse(point, vec2(0.66, 0.2));
    float shotTrail = 0.5 + 0.5 * sin(time * 12.0 - point.x * 14.0);
    glowMask = maskEllipse(point, vec2(1.0, 0.34)) * (0.24 + shotTrail * 0.21);
    detailColor += vec3(1.0, 0.92, 0.55) * shotTrail * baseMask * 0.24;
  } else {
    baseMask = maskRoundedBox(point, vec2(0.82, 0.12), 0.08);
    float railPulse = 0.5 + 0.5 * sin(time * 9.0 - point.x * 10.0);
    glowMask = maskRoundedBox(point, vec2(1.0, 0.22), 0.1) * (0.2 + railPulse * 0.2);
    detailColor += vec3(1.0, 0.62, 0.32) * railPulse * baseMask * 0.22;
  }

  float alpha = clamp((baseMask + glowMask) * vOpacity, 0.0, 1.0);
  if (alpha < 0.01) {
    discard;
  }

  vec3 finalColor = (color + detailColor) * (baseMask + glowMask * 0.95) * emissiveBoost;
  gl_FragColor = vec4(finalColor, alpha);
}
