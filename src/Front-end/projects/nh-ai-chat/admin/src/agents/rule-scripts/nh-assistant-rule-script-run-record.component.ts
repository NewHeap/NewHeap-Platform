import { DatePipe, KeyValuePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  RuleScriptRunRecord,
  RuleScriptSubjectRun,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { ruleScriptCodeKeys, ruleScriptRunCompleted, ruleScriptRunTone } from './rule-scripts';

/**
 * What a version of a rule script did, or would do, per subject (internal): a dry run, a shadow run or
 * the latest dry run that going live shows (RB-5). Per subject its status with the script's code and
 * where it stopped, the end it chose, every tool call in order, the writes with what they would have
 * become and their input, the proposals, the alerts it would raise and resolve, and its log. Nothing of it
 * happened: the record says what would have.
 */
@Component({
  selector: 'nh-assistant-rule-script-run-record',
  standalone: true,
  imports: [DatePipe, KeyValuePipe, TranslatePipe, NhAssistantTranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-rule-script-run-record.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './rule-scripts.scss']
})
export class NhAssistantRuleScriptRunRecordComponent {
  readonly record = input.required<RuleScriptRunRecord>();
  /** The names of the people who started a dry run, once known. */
  readonly names = input<NhAssistantAccountNames | null>(null);

  /** The code a write gets in shadow and in a dry run: it was recorded and did not happen. */
  readonly dryRunWrite = 'assistant-rule-script-dry-run';
  readonly completed = computed(() => ruleScriptRunCompleted(this.record()));
  readonly codeKeys = ruleScriptCodeKeys;
  readonly toneOf = ruleScriptRunTone;

  subjectName(subject: RuleScriptSubjectRun): string {
    return subject.displayName ?? subject.subjectId;
  }

  /** The first subject opens, and every subject that did not complete, so a problem is never hidden. */
  opens(subject: RuleScriptSubjectRun, index: number): boolean {
    return index === 0 || subject.status !== 'completed';
  }

  accountName(actorId: string): string {
    return this.names()?.nameOf(actorId) ?? actorId;
  }

  accountTitle(actorId: string): string | null {
    return this.names()?.idTitleOf(actorId) ?? null;
  }
}
