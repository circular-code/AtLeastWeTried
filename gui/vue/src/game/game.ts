export class Game {
  private readonly host: HTMLElement
  private surface: HTMLDivElement | null = null

  constructor(host: HTMLElement) {
    this.host = host
  }

  mount() {
    const surface = document.createElement('div')
    surface.className = 'game-surface'
    surface.textContent = 'Game layer'

    this.host.replaceChildren(surface)
    this.surface = surface
  }

  destroy() {
    if (this.surface && this.surface.parentElement === this.host) {
      this.host.removeChild(this.surface)
    }

    this.surface = null
  }
}
