import * as THREE from 'three'

export class Game {
  private readonly host: HTMLElement
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.PerspectiveCamera(75, 1, 0.1, 1000)
  private readonly renderer = new THREE.WebGLRenderer({ antialias: true })
  private readonly geometry = new THREE.BoxGeometry(1, 1, 1)
  private readonly material = new THREE.MeshNormalMaterial()
  private readonly cube = new THREE.Mesh(this.geometry, this.material)
  private animationFrameId: number | null = null
  private resizeObserver: ResizeObserver | null = null

  constructor(host: HTMLElement) {
    this.host = host
  }

  mount() {
    this.renderer.setPixelRatio(window.devicePixelRatio)
    this.renderer.domElement.className = 'game-surface'

    this.scene.add(this.cube)
    this.camera.position.z = 3

    this.host.replaceChildren(this.renderer.domElement)
    this.updateSize()

    this.resizeObserver = new ResizeObserver(() => {
      this.updateSize()
    })
    this.resizeObserver.observe(this.host)

    this.animate()
  }

  destroy() {
    if (this.animationFrameId !== null) {
      cancelAnimationFrame(this.animationFrameId)
      this.animationFrameId = null
    }

    this.resizeObserver?.disconnect()
    this.resizeObserver = null

    this.geometry.dispose()
    this.material.dispose()
    this.renderer.dispose()

    if (this.renderer.domElement.parentElement === this.host) {
      this.host.removeChild(this.renderer.domElement)
    }
  }

  private animate = () => {
    this.animationFrameId = requestAnimationFrame(this.animate)

    this.cube.rotation.x += 0.01
    this.cube.rotation.y += 0.01

    this.renderer.render(this.scene, this.camera)
  }

  private updateSize() {
    const width = this.host.clientWidth || window.innerWidth
    const height = this.host.clientHeight || window.innerHeight

    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    this.renderer.setSize(width, height)
  }
}
