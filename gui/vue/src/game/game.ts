import * as THREE from 'three'

type CircleData = {
  x: number
  y: number
  radius: number
  color?: THREE.ColorRepresentation
  segments?: number
}

export class Game {
  private readonly host: HTMLElement
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.OrthographicCamera()
  private readonly renderer = new THREE.WebGLRenderer({ antialias: true })
  private readonly circles: CircleData[] = []
  private readonly pointer = new THREE.Vector2()
  private activeCircle: CircleData | null = null
  private dragOffset = new THREE.Vector2()
  private isDown = false
  private moveFrameId: number | null = null
  private readonly renderObjects: THREE.LineLoop<THREE.BufferGeometry, THREE.LineBasicMaterial>[] = []

  constructor(host: HTMLElement) {
    this.host = host
  }

  mount() {
    this.renderer.setPixelRatio(window.devicePixelRatio)
    this.renderer.domElement.className = 'game-surface'
    this.renderer.setClearColor(0x020617, 1)

    this.addCircle({
      x: 0,
      y: 0,
      radius: 120,
    })
    this.addCircle({
      x: 50,
      y: -50,
      radius: 120,
    })
    this.addCircle({
      x: 150,
      y: -150,
      radius: 120,
    })
    this.camera.position.z = 1

    this.host.replaceChildren(this.renderer.domElement)
    this.updateSize()

    this.renderer.domElement.addEventListener('mousedown', this.onMouseDown)
    window.addEventListener('mouseup', this.onMouseUp)
    window.addEventListener('mousemove', this.onMouseMove)
  }

  destroy() {
    this.renderer.domElement.removeEventListener('mousedown', this.onMouseDown)
    window.removeEventListener('mouseup', this.onMouseUp)
    window.removeEventListener('mousemove', this.onMouseMove)

    if (this.moveFrameId !== null) {
      cancelAnimationFrame(this.moveFrameId)
      this.moveFrameId = null
    }

    this.clearRenderObjects()
    this.circles.length = 0
    this.activeCircle = null
    this.renderer.dispose()

    if (this.renderer.domElement.parentElement === this.host) {
      this.host.removeChild(this.renderer.domElement)
    }
  }

  private updateSize() {
    const width = this.host.clientWidth || window.innerWidth
    const height = this.host.clientHeight || window.innerHeight
    const halfWidth = width / 2
    const halfHeight = height / 2

    this.camera.left = -halfWidth
    this.camera.right = halfWidth
    this.camera.top = halfHeight
    this.camera.bottom = -halfHeight
    this.camera.updateProjectionMatrix()
    this.renderer.setSize(width, height)
    this.renderer.render(this.scene, this.camera)
  }

  private onMouseDown = (event: MouseEvent) => {
    this.isDown = true

    const pointer = this.getWorldPointer(event)
    const circle = this.getCircleAt(pointer)

    if (!circle) {
      return
    }

    this.activeCircle = circle
    this.dragOffset.set(
      pointer.x - circle.x,
      pointer.y - circle.y,
    )
  }

  private onMouseUp = () => {
    this.isDown = false
    this.activeCircle = null
  }

  private onMouseMove = (event: MouseEvent) => {
    this.pointer.copy(this.getWorldPointer(event))

    if (this.moveFrameId !== null) {
      return
    }

    this.moveFrameId = requestAnimationFrame(() => {
      this.moveFrameId = null

      if (!this.isDown || !this.activeCircle) {
        return
      }

      this.activeCircle.x = this.pointer.x - this.dragOffset.x
      this.activeCircle.y = this.pointer.y - this.dragOffset.y
      this.renderCircles()
    })
  }

  private addCircle({
    x,
    y,
    radius,
    color = 0xf8fafc,
    segments = 96,
  }: CircleData) {
    this.circles.push({
      x,
      y,
      radius,
      color,
      segments,
    })

    this.renderCircles()
  }

  private getCircleAt(pointer: THREE.Vector2) {
    for (let index = this.circles.length - 1; index >= 0; index -= 1) {
      const circle = this.circles[index]

      if (!circle) {
        continue
      }

      const center = new THREE.Vector2(circle.x, circle.y)
      const distance = pointer.distanceTo(center)

      if (distance <= circle.radius) {
        return circle
      }
    }

    return null
  }

  private getWorldPointer(event: MouseEvent) {
    const rect = this.renderer.domElement.getBoundingClientRect()
    const normalizedX = ((event.clientX - rect.left) / rect.width) * 2 - 1
    const normalizedY = -(((event.clientY - rect.top) / rect.height) * 2 - 1)
    const worldPoint = new THREE.Vector3(normalizedX, normalizedY, 0)

    worldPoint.unproject(this.camera)

    return new THREE.Vector2(worldPoint.x, worldPoint.y)
  }

  private renderCircles() {
    this.clearRenderObjects()

    for (const circle of this.circles) {
      const curve = new THREE.ArcCurve(0, 0, circle.radius, 0, Math.PI * 2)
      const points = curve.getPoints(circle.segments ?? 96)
      const geometry = new THREE.BufferGeometry().setFromPoints(points)
      const material = new THREE.LineBasicMaterial({ color: circle.color ?? 0xf8fafc })
      const renderObject =
        new THREE.LineLoop<THREE.BufferGeometry, THREE.LineBasicMaterial>(geometry, material)

      renderObject.position.set(circle.x, circle.y, 0)
      this.scene.add(renderObject)
      this.renderObjects.push(renderObject)
    }

    this.renderer.render(this.scene, this.camera)
  }

  private clearRenderObjects() {
    for (const renderObject of this.renderObjects) {
      this.scene.remove(renderObject)
      renderObject.geometry.dispose()
      renderObject.material.dispose()
    }

    this.renderObjects.length = 0
  }
}
