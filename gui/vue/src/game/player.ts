import * as THREE from 'three'
import { World } from './world'

export class Player {
  x: number
  y: number
  speed: number
  angle: number
  size: number

  constructor({
    x = 0,
    y = 0,
    speed = 240,
    angle = 0,
    size = 18,
  }: Partial<Player> = {}) {
    this.x = x
    this.y = y
    this.speed = speed
    this.angle = angle
    this.size = size
  }

  move(direction: THREE.Vector2, deltaSeconds: number, world: World) {
    const distance = this.speed * deltaSeconds
    const previousX = this.x
    const previousY = this.y

    this.x = world.clampX(this.x + direction.x * distance, this.size)
    this.y = world.clampY(this.y + direction.y * distance, this.size)
    this.angle = Math.atan2(direction.y, direction.x)

    return previousX !== this.x || previousY !== this.y
  }
}
