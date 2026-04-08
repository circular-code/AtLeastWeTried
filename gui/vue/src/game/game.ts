import * as THREE from 'three'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { CircleObject } from './object'
import { createObjectMaterial } from './objectMaterial'
import { Player } from './player'
import { World } from './world'

export class Game {
  private readonly host: HTMLElement
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.OrthographicCamera()
  private readonly renderer = new THREE.WebGLRenderer({ antialias: true })
  private readonly composer = new EffectComposer(this.renderer)
  private readonly renderPass = new RenderPass(this.scene, this.camera)
  private readonly bloomPass = new UnrealBloomPass(new THREE.Vector2(1, 1), 1.1, 0.8, 0.15)
  private readonly outputPass = new OutputPass()
  private readonly circles: CircleObject[] = []
  private readonly pointer = new THREE.Vector2()
  private readonly pressedKeys = new Set<string>()
  private readonly viewportSize = new THREE.Vector2()
  private readonly world = new World(10000, 10000)
  private readonly player = new Player()
  private readonly enableBloom = true
  private zoom = 1
  private readonly minZoom = 0.5
  private readonly maxZoom = 3
  private activeCircle: CircleObject | null = null
  private dragOffset = new THREE.Vector2()
  private isDown = false
  private moveFrameId: number | null = null
  private loopFrameId: number | null = null
  private lastFrameTime: number | null = null
  private readonly objectGeometry = new THREE.PlaneGeometry(2, 2)
  private readonly objectMaterial = createObjectMaterial()
  private readonly instanceTransform = new THREE.Object3D()
  private objectRenderMesh: THREE.InstancedMesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial> | null = null
  private readonly playerGeometry = new THREE.BufferGeometry()
  private readonly playerMaterial = new THREE.LineBasicMaterial({ color: 0xf97316 })
  private readonly playerRenderObject =
    new THREE.LineLoop<THREE.BufferGeometry, THREE.LineBasicMaterial>(
      this.playerGeometry,
      this.playerMaterial,
    )

  constructor(host: HTMLElement) {
    this.host = host

    this.composer.addPass(this.renderPass)
    this.composer.addPass(this.bloomPass)
    this.composer.addPass(this.outputPass)
  }

  mount() {
    this.renderer.setPixelRatio(window.devicePixelRatio)
    this.renderer.domElement.className = 'game-surface'
    this.renderer.setClearColor(0x020617, 1)
    this.camera.position.z = 1

    this.buildPlayerGeometry()
    this.scene.add(this.playerRenderObject)
    this.host.replaceChildren(this.renderer.domElement)
    this.updateViewportSize()
    this.generateCircles()
    this.updatePlayerRenderObject()

    this.renderer.domElement.addEventListener('mousedown', this.onMouseDown)
    this.renderer.domElement.addEventListener('wheel', this.onWheel, { passive: false })
    window.addEventListener('keydown', this.onKeyDown)
    window.addEventListener('keyup', this.onKeyUp)
    window.addEventListener('mouseup', this.onMouseUp)
    window.addEventListener('mousemove', this.onMouseMove)
    this.loopFrameId = requestAnimationFrame(this.update)
  }

  destroy() {
    this.renderer.domElement.removeEventListener('mousedown', this.onMouseDown)
    this.renderer.domElement.removeEventListener('wheel', this.onWheel)
    window.removeEventListener('keydown', this.onKeyDown)
    window.removeEventListener('keyup', this.onKeyUp)
    window.removeEventListener('mouseup', this.onMouseUp)
    window.removeEventListener('mousemove', this.onMouseMove)

    if (this.moveFrameId !== null) {
      cancelAnimationFrame(this.moveFrameId)
      this.moveFrameId = null
    }

    if (this.loopFrameId !== null) {
      cancelAnimationFrame(this.loopFrameId)
      this.loopFrameId = null
    }

    this.clearRenderObjects()
    this.circles.length = 0
    this.pressedKeys.clear()
    this.activeCircle = null
    this.scene.remove(this.playerRenderObject)
    this.composer.dispose()
    this.renderPass.dispose()
    this.bloomPass.dispose()
    this.outputPass.dispose()
    this.objectGeometry.dispose()
    this.objectMaterial.dispose()
    this.playerGeometry.dispose()
    this.playerMaterial.dispose()
    this.renderer.dispose()

    if (this.renderer.domElement.parentElement === this.host) {
      this.host.removeChild(this.renderer.domElement)
    }
  }

  private updateViewportSize() {
    const width = this.host.clientWidth || window.innerWidth
    const height = this.host.clientHeight || window.innerHeight

    this.viewportSize.set(width, height)
    this.renderer.setSize(width, height)
    this.composer.setSize(width, height)
    this.composer.setPixelRatio(window.devicePixelRatio)
    this.bloomPass.resolution.set(width, height)
    this.updateCamera()
  }

  private updateCamera() {
    const halfWidth = this.viewportSize.x / 2 / this.zoom
    const halfHeight = this.viewportSize.y / 2 / this.zoom
    const maxCameraX = Math.max(this.world.halfWidth - halfWidth, 0)
    const maxCameraY = Math.max(this.world.halfHeight - halfHeight, 0)

    this.camera.left = -halfWidth
    this.camera.right = halfWidth
    this.camera.top = halfHeight
    this.camera.bottom = -halfHeight
    this.camera.position.x = THREE.MathUtils.clamp(this.player.x, -maxCameraX, maxCameraX)
    this.camera.position.y = THREE.MathUtils.clamp(this.player.y, -maxCameraY, maxCameraY)
    this.camera.updateProjectionMatrix()
    this.renderScene()
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

  private onKeyDown = (event: KeyboardEvent) => {
    this.pressedKeys.add(event.key.toLowerCase())
  }

  private onKeyUp = (event: KeyboardEvent) => {
    this.pressedKeys.delete(event.key.toLowerCase())
  }

  private onWheel = (event: WheelEvent) => {
    event.preventDefault()

    const zoomDelta = Math.exp(-event.deltaY * 0.0015)
    this.zoom = THREE.MathUtils.clamp(
      this.zoom * zoomDelta,
      this.minZoom,
      this.maxZoom,
    )

    this.updateCamera()
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

      this.activeCircle.moveTo(
        this.pointer.x - this.dragOffset.x,
        this.pointer.y - this.dragOffset.y,
        this.world,
      )
      this.renderCircles()
    })
  }

  private update = (timestamp: number) => {
    const deltaSeconds =
      this.lastFrameTime === null ? 0 : (timestamp - this.lastFrameTime) / 1000
    this.lastFrameTime = timestamp

    const didMove = this.updatePlayer(deltaSeconds)

    if (didMove) {
      this.updateCamera()
    }

    this.loopFrameId = requestAnimationFrame(this.update)
  }

  private addCircle(circle: CircleObject, shouldRender = true) {
    this.circles.push(circle)

    if (shouldRender) {
      this.renderCircles()
    }
  }

  private generateCircles(count = 5000) {
    this.circles.length = 0

    const halfWidth = this.world.halfWidth
    const halfHeight = this.world.halfHeight

    for (let index = 0; index < count; index += 1) {
      const radius = 8 + Math.random() * 20
      const x = THREE.MathUtils.randFloat(-halfWidth + radius, halfWidth - radius)
      const y = THREE.MathUtils.randFloat(-halfHeight + radius, halfHeight - radius)

      this.addCircle(
        new CircleObject({
          x,
          y,
          radius,
          segments: 48,
        }),
        false,
      )
    }

    this.renderCircles()
  }

  private getCircleAt(pointer: THREE.Vector2) {
    for (let index = this.circles.length - 1; index >= 0; index -= 1) {
      const circle = this.circles[index]

      if (!circle) {
        continue
      }

      if (circle.containsPoint(pointer)) {
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

  private updatePlayer(deltaSeconds: number) {
    const horizontal =
      (this.pressedKeys.has('d') ? 1 : 0) - (this.pressedKeys.has('a') ? 1 : 0)
    const vertical =
      (this.pressedKeys.has('w') ? 1 : 0) - (this.pressedKeys.has('s') ? 1 : 0)

    if (horizontal === 0 && vertical === 0) {
      return false
    }

    const direction = new THREE.Vector2(horizontal, vertical).normalize()
    const didMove = this.player.move(direction, deltaSeconds, this.world)

    if (didMove) {
      this.updatePlayerRenderObject()
    }

    return didMove
  }

  private buildPlayerGeometry() {
    const { size } = this.player
    const points = [
      new THREE.Vector3(size, 0, 0),
      new THREE.Vector3(-size * 0.75, size * 0.55, 0),
      new THREE.Vector3(-size * 0.75, -size * 0.55, 0),
    ]

    this.playerGeometry.setFromPoints(points)
  }

  private updatePlayerRenderObject() {
    this.playerRenderObject.position.set(this.player.x, this.player.y, 0)
    this.playerRenderObject.rotation.z = this.player.angle
  }

  private renderCircles() {
    if (!this.objectRenderMesh || this.objectRenderMesh.count !== this.circles.length) {
      this.rebuildObjectRenderMesh()
    }

    if (!this.objectRenderMesh) {
      return
    }

    for (let index = 0; index < this.circles.length; index += 1) {
      const circle = this.circles[index]

      if (!circle) {
        continue
      }

      this.instanceTransform.position.set(circle.x, circle.y, 0)
      this.instanceTransform.scale.set(circle.radius, circle.radius, 1)
      this.instanceTransform.updateMatrix()

      this.objectRenderMesh.setMatrixAt(index, this.instanceTransform.matrix)
    }

    this.objectRenderMesh.instanceMatrix.needsUpdate = true

    this.renderScene()
  }

  private clearRenderObjects() {
    if (this.objectRenderMesh) {
      this.scene.remove(this.objectRenderMesh)
      this.objectRenderMesh.dispose()
      this.objectRenderMesh = null
    }
  }

  private rebuildObjectRenderMesh() {
    this.clearRenderObjects()

    this.objectRenderMesh = new THREE.InstancedMesh(
      this.objectGeometry,
      this.objectMaterial,
      this.circles.length,
    )
    this.objectRenderMesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage)
    this.scene.add(this.objectRenderMesh)
  }

  private renderScene() {
    if (this.enableBloom) {
      this.composer.render()
      return
    }

    this.renderer.render(this.scene, this.camera)
  }
}
