import * as THREE from 'three'
import { World } from './world'

type CircleObjectOptions = {
  x: number
  y: number
  radius: number
  color?: THREE.ColorRepresentation
  segments?: number
}

export class CircleObject {
  x: number
  y: number
  radius: number
  color?: THREE.ColorRepresentation
  segments?: number

  constructor({
    x,
    y,
    radius,
    color = 0xf8fafc,
    segments = 96,
  }: CircleObjectOptions) {
    this.x = x
    this.y = y
    this.radius = radius
    this.color = color
    this.segments = segments
  }

  containsPoint(pointer: THREE.Vector2) {
    const center = new THREE.Vector2(this.x, this.y)
    return pointer.distanceTo(center) <= this.radius
  }

  moveTo(x: number, y: number, world: World) {
    this.x = world.clampX(x, this.radius)
    this.y = world.clampY(y, this.radius)
  }
}
