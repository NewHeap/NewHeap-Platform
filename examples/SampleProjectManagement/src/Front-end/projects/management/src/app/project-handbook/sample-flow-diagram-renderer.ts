import { NhAssistantDiagramRenderer } from '@newheap/platform-ai-chat';

/**
 * SPM-336: the diagram renderer the portal supplies to `nh-assistant-document`. The sample has no
 * diagram package, so it draws its own `flow` language; the drawing module loads only when a
 * document has a `flow` block, as an application loads Mermaid or another library lazily in
 * `render`. A rejected promise carries the reason the document shows next to the source.
 */
export const sampleFlowDiagramRenderer: NhAssistantDiagramRenderer = {
  languages: ['flow'],
  async render(source: string): Promise<string> {
    const { drawSampleFlow } = await import('./sample-flow-diagram');
    return drawSampleFlow(source);
  }
};
