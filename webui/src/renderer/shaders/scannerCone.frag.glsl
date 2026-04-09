uniform float time;
uniform float halfWidthRad;
uniform float coneLength;
uniform float targetHalfWidthRad;
uniform float targetLength;
uniform vec3 color;
uniform float opacity;

varying vec2 vLocalUv;

const float PI = 3.14159265359;

void main() {
  // vLocalUv goes -1..+1; the cone points in +X direction (angle=0)
  float angle = atan(vLocalUv.y, vLocalUv.x);

  // Discard behind origin
  if (vLocalUv.x < -0.02) discard;

  float r = length(vLocalUv);
  // Normalize radial distance: 0 at center, 1 at cone range
  float radialNorm = r;

  // Angular mask: inside the current scan cone half-angle
  float absAngle = abs(angle);
  float edgeSoftness = 0.006;
  float angleMask = 1.0 - smoothstep(halfWidthRad - edgeSoftness, halfWidthRad + edgeSoftness, absAngle);

  // Radial mask: hard cutoff at range
  float radialMask = 1.0 - step(1.0, radialNorm);

  // Combine
  float coneMask = angleMask * radialMask;

  // Sweep effect: radial pulse lines
  float sweep = 0.5 + 0.5 * sin(radialNorm * 28.0 - time * 3.0);
  float sweepMask = sweep * 0.3;

  // Inner glow near origin
  float innerGlow = (1.0 - smoothstep(0.0, 0.35, radialNorm)) * 0.4;

  // Edge highlight — thin crisp border along the cone sides
  float edgeInner = smoothstep(halfWidthRad - 0.012, halfWidthRad - 0.004, absAngle);
  float edgeOuter = 1.0 - smoothstep(halfWidthRad - 0.004, halfWidthRad + 0.004, absAngle);
  float edgeGlow = edgeInner * edgeOuter * radialMask * 0.8;

  // Radial arc at the range limit
  float arcEdge = smoothstep(0.96, 0.98, radialNorm) * (1.0 - smoothstep(0.99, 1.0, radialNorm)) * angleMask * 0.7;

  // Target cone outline (faint dashed)
  float targetAngleMask = smoothstep(targetHalfWidthRad - 0.005, targetHalfWidthRad, absAngle)
                        * (1.0 - smoothstep(targetHalfWidthRad, targetHalfWidthRad + 0.008, absAngle));
  float targetRadialEdge = smoothstep(targetLength - 0.01, targetLength, radialNorm)
                         * (1.0 - smoothstep(targetLength, targetLength + 0.008, radialNorm));
  float targetInsideAngle = 1.0 - smoothstep(targetHalfWidthRad - 0.004, targetHalfWidthRad + 0.004, absAngle);
  float dash = step(0.5, fract(radialNorm * 12.0));
  float targetOutline = (targetAngleMask * radialMask + targetRadialEdge * targetInsideAngle) * dash * 0.35;

  float alpha = (coneMask * (0.08 + sweepMask + innerGlow) + edgeGlow + arcEdge + targetOutline) * opacity;

  if (alpha < 0.005) discard;

  vec3 finalColor = color * (1.0 + innerGlow * 0.8 + edgeGlow * 1.2);
  gl_FragColor = vec4(finalColor, alpha);
}
