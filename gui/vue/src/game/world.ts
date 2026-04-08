export class World {
  readonly width: number
  readonly height: number

  constructor(width: number, height: number) {
    this.width = width
    this.height = height
  }

  get halfWidth() {
    return this.width / 2
  }

  get halfHeight() {
    return this.height / 2
  }

  clampX(value: number, padding = 0) {
    return Math.min(Math.max(value, -this.halfWidth + padding), this.halfWidth - padding)
  }

  clampY(value: number, padding = 0) {
    return Math.min(Math.max(value, -this.halfHeight + padding), this.halfHeight - padding)
  }
}
