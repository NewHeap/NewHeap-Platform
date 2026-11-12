import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/** Internal capture boundary; the application never uploads a capture before preview confirmation. */
@Injectable({ providedIn: 'root' })
export class NhAssistantScreenshotCapture {
  private readonly document = inject(DOCUMENT);

  async capture(capturing: () => void, signal: AbortSignal): Promise<File> {
    const { default: html2canvas } = await import('html2canvas');
    signal.throwIfAborted();
    capturing();

    const window = this.document.defaultView!;
    // Let the capturing status paint before the renderer starts its synchronous work.
    await new Promise<void>(resolve => window.setTimeout(resolve, 0));
    signal.throwIfAborted();
    let cloneFrame: Element | null = null;
    let canvas: HTMLCanvasElement | undefined;
    try {
      canvas = await html2canvas(this.document.body, {
        logging: false,
        allowTaint: false,
        useCORS: false,
        imageTimeout: 5_000,
        x: window.scrollX,
        y: window.scrollY,
        width: window.innerWidth,
        height: window.innerHeight,
        windowWidth: window.innerWidth,
        windowHeight: window.innerHeight,
        // ponytail: cap the longest edge at 1920 pixels; add region selection if finer detail is needed.
        scale: Math.min(1, 1920 / Math.max(window.innerWidth, window.innerHeight)),
        ignoreElements: element => element.matches('nh-assistant-panel, nh-assistant-launcher, .nh-assistant-overlay-pane, nh-assistant-screenshot'),
        onclone: document => {
          cloneFrame = document.defaultView?.frameElement ?? null;
          signal.throwIfAborted();
          // Hide in the clone only, keeping page layout and the live form unchanged.
          for (const element of Array.from(document.querySelectorAll<HTMLElement>(
            '[data-nh-assistant-screenshot-exclude], input[type="password"], input[autocomplete="one-time-code"], input[autocomplete^="cc-"]'))) {
            element.style.setProperty('visibility', 'hidden', 'important');
          }
          // Let the browser resolve modern computed colors that html2canvas 1.x cannot parse.
          const pixel = document.createElement('canvas');
          pixel.width = pixel.height = 1;
          const context = pixel.getContext('2d')!;
          const colors = new Map<string, string>();
          const rgb = (color: string): string => {
            let value = colors.get(color);
            if (!value) {
              context.clearRect(0, 0, 1, 1);
              context.fillStyle = color;
              context.fillRect(0, 0, 1, 1);
              const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data;
              value = `rgba(${r}, ${g}, ${b}, ${a / 255})`;
              colors.set(color, value);
            }
            return value;
          };
          const properties = ['color', 'background-color', 'background-image', 'border-top-color',
            'border-right-color', 'border-bottom-color', 'border-left-color', 'box-shadow', 'text-shadow', 'text-decoration-color'];
          for (const element of Array.from(document.querySelectorAll<HTMLElement>('html, body, body *'))) {
            const style = document.defaultView!.getComputedStyle(element);
            for (const property of properties) {
              const value = style.getPropertyValue(property);
              // ponytail: computed colors have flat arguments; replace this bridge when the renderer supports CSS Color 4.
              const converted = value.replace(/\b(?:color|oklch|oklab|lab|lch)\([^()]*\)/g, rgb);
              if (converted !== value) {
                element.style.setProperty(property, converted, 'important');
              }
            }
          }
        }
      });
      signal.throwIfAborted();
      const blob = await new Promise<Blob>((resolve, reject) => canvas!.toBlob(value => {
        if (value) {
          resolve(value);
        } else {
          reject(new Error('Screenshot encoding failed.'));
        }
      }, 'image/png'));
      signal.throwIfAborted();
      return new File([blob], 'page-screenshot.png', { type: 'image/png' });
    } finally {
      // html2canvas only removes its clone on success; failures must release it too.
      (cloneFrame as Element | null)?.remove();
      if (canvas) {
        canvas.width = 0;
        canvas.height = 0;
      }
    }
  }
}
