// The public declarations the API snapshot records, read from source text. An enum is recorded with
// its members, so a new, removed or renumbered value is a reviewed public API change like a new member
// of a class.

/** `line` without a trailing `//` comment; a `//` inside a quoted string, such as a URL, stays. */
function withoutLineComment(line) {
  let quote = null;
  for (let index = 0; index < line.length; index += 1) {
    const character = line[index];
    if (quote) {
      if (character === '\\') {
        index += 1;
      } else if (character === quote) {
        quote = null;
      }
    } else if (character === '"' || character === "'" || character === '`') {
      quote = character;
    } else if (character === '/' && line[index + 1] === '/') {
      return line.slice(0, index).trimEnd();
    }
  }
  return line;
}

/** The members of an enum body, `{ A = 0, B = 1 }`, from the lines after its opening brace. */
function enumBody(lines, start) {
  const members = [];
  let index = start;
  for (; index < lines.length; index += 1) {
    const line = lines[index].trim();
    if (line.startsWith('}')) {
      break;
    }
    if (!line || line.startsWith('//')) {
      continue;
    }
    members.push(withoutLineComment(line));
  }
  const body = members.join(' ').replace(/\s+/g, ' ').trim().replace(/,$/, '');
  return { text: `{ ${body} }`.replace('{  }', '{ }'), end: index };
}

/** Whether a declaration opens an enum body that its own lines do not close. */
function opensEnumBody(declaration, keyword) {
  return keyword.test(declaration) && declaration.includes('{') && !declaration.includes('}');
}

/**
 * The public and protected C# declarations of one source file, with the attributes above each, as one
 * normalized line per declaration. A declaration ends at its first `{`, `;`, `=` or `=>` outside
 * parentheses; an enum also takes its members.
 * @param {string} text
 * @returns {string[]}
 */
export function backEndDeclarations(text) {
  const lines = text.split(/\r?\n/);
  const declarations = [];
  let attributes = [];
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index].trim();
    if (line.startsWith('[')) {
      attributes.push(line.replace(/\s+/g, ' '));
      continue;
    }
    if (!/^(public|protected)\b/.test(line)) {
      if (line && !line.startsWith('//')) attributes = [];
      continue;
    }
    const declaration = [line];
    let parentheses = (line.match(/\(/g) ?? []).length - (line.match(/\)/g) ?? []).length;
    while (index + 1 < lines.length && (parentheses > 0 || !/[{;=]|=>/.test(declaration.at(-1)))) {
      index += 1;
      const continuation = lines[index].trim();
      declaration.push(continuation);
      parentheses += (continuation.match(/\(/g) ?? []).length - (continuation.match(/\)/g) ?? []).length;
      if (declaration.length > 30) break;
    }
    let text = [...attributes, ...declaration].join(' ').replace(/\s+/g, ' ').trim();
    if (opensEnumBody(text, /\benum\b/)) {
      const body = enumBody(lines, index + 1);
      text = text.replace(/\s*\{$/, '') + ' ' + body.text;
      index = body.end;
    }
    declarations.push(text);
    attributes = [];
  }
  return declarations;
}

/**
 * The exported and public declarations and the Angular decorators of one TypeScript source file, one
 * line each; an exported enum also takes its members.
 * @param {string} text
 * @returns {string[]}
 */
export function frontEndDeclarations(text) {
  const lines = text.split(/\r?\n/);
  const declarations = [];
  for (let index = 0; index < lines.length; index += 1) {
    const declaration = lines[index].trim().replace(/\s+/g, ' ');
    if (!/^(export|public|protected)\b/.test(declaration) && !/^@(Input|Output|Directive|Component|Injectable)\b/.test(declaration)) {
      continue;
    }
    if (opensEnumBody(declaration, /^export (const )?enum\b/)) {
      const body = enumBody(lines, index + 1);
      declarations.push(declaration.replace(/\s*\{$/, '') + ' ' + body.text);
      index = body.end;
      continue;
    }
    declarations.push(declaration);
  }
  return declarations;
}
