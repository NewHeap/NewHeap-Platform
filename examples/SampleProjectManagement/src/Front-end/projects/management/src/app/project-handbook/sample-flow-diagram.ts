/**
 * SPM-336: the portal's own small diagram language, drawn without a diagram package. Each line is
 * a chain of steps, `Draft -> Review -> Done`; steps with the same name are one step. The steps are
 * laid out left to right by their longest chain of predecessors. This module is imported only when
 * a document has a `flow` block (see `sampleFlowDiagramRenderer`), as an application would load a
 * real drawing library such as Mermaid lazily.
 */

const maxSteps = 24;
const maxLabel = 18;
const boxWidth = 150;
const boxHeight = 40;
const columnGap = 56;
const rowGap = 18;
const margin = 8;

interface FlowStep {
  readonly name: string;
  readonly index: number;
}

function escapeXml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function parse(source: string): { steps: FlowStep[]; edges: [number, number][] } {
  const steps = new Map<string, FlowStep>();
  const edges: [number, number][] = [];
  const stepOf = (name: string): FlowStep => {
    let step = steps.get(name);
    if (!step) {
      step = { name, index: steps.size };
      steps.set(name, step);
    }
    return step;
  };

  const lines = source.split(/\r?\n/);
  for (const [lineIndex, line] of lines.entries()) {
    if (!line.trim()) {
      continue;
    }

    const names = line.split('->').map(name => name.trim());
    if (names.length < 2) {
      throw new Error(`Line ${lineIndex + 1}: write the steps as A -> B.`);
    }
    if (names.some(name => !name)) {
      throw new Error(`Line ${lineIndex + 1}: a step has no name.`);
    }

    for (let index = 1; index < names.length; index++) {
      edges.push([stepOf(names[index - 1]).index, stepOf(names[index]).index]);
    }
  }

  if (steps.size === 0) {
    throw new Error('The flow has no steps.');
  }
  if (steps.size > maxSteps) {
    throw new Error(`The flow has more than ${maxSteps} steps.`);
  }

  return { steps: [...steps.values()], edges };
}

/** The column of each step: the length of its longest chain of predecessors. */
function columnsOf(steps: FlowStep[], edges: [number, number][]): number[] {
  const columns = steps.map(() => 0);
  // A chain longer than the number of steps can only come from a cycle.
  for (let round = 0; round <= steps.length; round++) {
    let changed = false;
    for (const [from, to] of edges) {
      if (columns[to] < columns[from] + 1) {
        columns[to] = columns[from] + 1;
        changed = true;
      }
    }

    if (!changed) {
      return columns;
    }
  }

  throw new Error('The flow goes round in a circle; draw it without a step that leads back.');
}

/** Draws a flow as SVG, or throws with a reason the handbook shows next to the source. */
export function drawSampleFlow(source: string): string {
  const { steps, edges } = parse(source);
  const columns = columnsOf(steps, edges);
  const rows = new Map<number, number>();
  const positions = steps.map(step => {
    const column = columns[step.index];
    const row = rows.get(column) ?? 0;
    rows.set(column, row + 1);
    return { x: margin + column * (boxWidth + columnGap), y: margin + row * (boxHeight + rowGap) };
  });
  const width = margin * 2 + (Math.max(...columns) + 1) * (boxWidth + columnGap) - columnGap;
  const height = margin * 2 + Math.max(...rows.values()) * (boxHeight + rowGap) - rowGap;

  const boxes = steps.map(step => {
    const { x, y } = positions[step.index];
    const label = step.name.length > maxLabel ? `${step.name.slice(0, maxLabel - 1)}…` : step.name;
    return `<g class="step"><title>${escapeXml(step.name)}</title>`
      + `<rect x="${x}" y="${y}" width="${boxWidth}" height="${boxHeight}" rx="10"/>`
      + `<text x="${x + boxWidth / 2}" y="${y + boxHeight / 2}">${escapeXml(label)}</text></g>`;
  });
  const lines = edges.map(([from, to]) => {
    const start = positions[from];
    const end = positions[to];
    return `<line x1="${start.x + boxWidth}" y1="${start.y + boxHeight / 2}" x2="${end.x - 4}" y2="${end.y + boxHeight / 2}"/>`;
  });

  return [
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}">`,
    `<title>Flow: ${escapeXml(steps.map(step => step.name).join(', '))}</title>`,
    '<style>',
    // The document's text color, so the drawing follows the light and dark schemes.
    '.step rect { fill: color-mix(in srgb, currentColor 8%, transparent); stroke: currentColor; stroke-width: 1.5; }',
    '.step text { fill: currentColor; font: 600 13px system-ui, sans-serif; text-anchor: middle; dominant-baseline: central; }',
    '.edges line { stroke: currentColor; stroke-width: 1.5; marker-end: url(#sample-flow-arrow); }',
    '</style>',
    '<defs><marker id="sample-flow-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">',
    '<path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor"/></marker></defs>',
    `<g class="edges">${lines.join('')}</g>`,
    boxes.join(''),
    '</svg>'
  ].join('');
}
