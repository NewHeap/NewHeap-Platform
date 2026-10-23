import {
  InstructionAssetList,
  InstructionAssetProblem,
  InstructionAssetReference,
  InstructionAssetText
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockDashCase, mockFailure as failure, mockHashText as hashText, mockMaxInstructions } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockInstructionAsset } from './nh-assistant-mock.models';

/**
 * The application's text assets of the mock API, as its `INhAiTextAssetSource` would supply them:
 * `GET admin/instruction-assets` lists them without content and `GET admin/instruction-assets/{id}/versions/{version}`
 * reads one. A skill version or an agent created in the administration that names one is checked as
 * the server checks it: the asset must exist with the hash the request names. `setAsset` and
 * `removeAsset` change the assets afterwards, so a reference that no longer resolves shows its problem
 * (`asset-changed`, `asset-missing`), as after the server's next load. Without `instructionAssets` in
 * the scenario the application offers none and the run catalog says so.
 */
export class NhAssistantMockInstructionAssets {
  private readonly assets = new Map<string, NhAssistantMockInstructionAsset>();

  constructor(assets: readonly NhAssistantMockInstructionAsset[] | undefined) {
    this.supported = assets !== undefined;
    for (const asset of assets ?? []) {
      this.assets.set(keyOf(asset.assetId, asset.version), { ...asset });
    }
  }

  /** Whether the scenario's application registers a text asset source. */
  readonly supported: boolean;

  /** Publishes or changes one version of an asset. */
  setAsset(asset: NhAssistantMockInstructionAsset): void {
    this.assets.set(keyOf(asset.assetId, asset.version), { ...asset });
  }

  /** Removes one version of an asset. */
  removeAsset(assetId: string, version: number): void {
    this.assets.delete(keyOf(assetId, version));
  }

  /** `admin/instruction-assets[/{id}/versions/{version}]`; `segments` start after `instruction-assets`. */
  handle(method: string, segments: string[]): NhAssistantMockResult {
    if (method !== 'GET') {
      return { status: 405 };
    }
    if (segments.length === 0) {
      const list: InstructionAssetList = {
        supported: this.supported,
        assets: [...this.assets.values()].map(asset => ({
          assetId: asset.assetId,
          version: asset.version,
          hash: hashText(asset.content),
          replacedByAssetId: asset.replacedByAssetId ?? null,
          deprecatedAt: asset.deprecatedAt ?? null
        }))
      };
      return { status: 200, body: list };
    }
    const [assetId, versions, version] = segments;
    const asset = segments.length === 3 && versions === 'versions' ? this.assets.get(keyOf(assetId, Number(version))) : undefined;
    if (!this.supported || !asset) {
      return failure('assistant-instructions-asset-not-found');
    }
    const text: InstructionAssetText = {
      assetId: asset.assetId,
      version: asset.version,
      hash: hashText(asset.content),
      content: asset.content,
      problem: null
    };
    return { status: 200, body: text };
  }

  /**
   * Adds the field errors of a reference a save names, as the server does: `instructionsAsset`
   * `not-allowed` without a source, `invalid` for a malformed reference, `not-found` for a missing
   * asset, and `instructionsAsset.hash` `invalid` for another hash.
   */
  check(reference: InstructionAssetReference, errors: Record<string, string[]>): void {
    if (!this.supported) {
      errors['instructionsAsset'] = ['not-allowed'];
      return;
    }
    if (typeof reference.assetId !== 'string' || !mockDashCase.test(reference.assetId) || !Number.isInteger(reference.version) || reference.version < 1) {
      errors['instructionsAsset'] = ['invalid'];
      return;
    }
    const problem = this.problemOf(reference);
    if (problem === 'asset-missing') {
      errors['instructionsAsset'] = ['not-found'];
    } else if (problem === 'asset-changed') {
      errors['instructionsAsset.hash'] = ['invalid'];
    } else if (problem !== null) {
      errors['instructionsAsset'] = ['invalid'];
    }
  }

  /** Why the reference does not resolve to its hash now, or `null` when it does. */
  problemOf(reference: InstructionAssetReference): InstructionAssetProblem | null {
    if (!this.supported) {
      return 'no-asset-source';
    }
    const asset = this.assets.get(keyOf(reference.assetId, reference.version));
    if (!asset) {
      return 'asset-missing';
    }
    if (asset.content.trim().length === 0 || asset.content.length > mockMaxInstructions) {
      return 'asset-invalid';
    }
    return hashText(asset.content) === reference.hash ? null : 'asset-changed';
  }

  /** The text the reference resolves to, or `null` when it does not resolve. */
  textOf(reference: InstructionAssetReference): string | null {
    return this.problemOf(reference) === null ? this.assets.get(keyOf(reference.assetId, reference.version))!.content : null;
  }
}

function keyOf(assetId: string, version: number): string {
  return `${assetId}@${version}`;
}
