import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output
} from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import {
  NhBackgroundOperation,
  NhBackgroundOperationChild,
  NhBackgroundOperationStatus,
  NhBackgroundOperationStep,
  NhBackgroundOperationStepStatus,
  nhBackgroundOperationStatusName,
  nhBackgroundOperationStepStatusName,
  nhBackgroundOperationTranslationSegment
} from '../../models/background-operation.models';

@Component({
  selector: 'nh-background-operation-progress',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './component.html',
  styleUrl: './component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NhBackgroundOperationProgressComponent {
  @Input({ required: true }) operation!: NhBackgroundOperation;
  @Input() showEvents = true;
  @Input() showActions = false;
  @Output() cancelRequested = new EventEmitter<string>();
  @Output() retryRequested = new EventEmitter<string>();

  statusKey(status: NhBackgroundOperationStatus): string {
    return `nh-background-operations.status.${nhBackgroundOperationTranslationSegment(nhBackgroundOperationStatusName(status))}`;
  }

  stepStatusKey(status: NhBackgroundOperationStepStatus): string {
    return `nh-background-operations.step-status.${nhBackgroundOperationTranslationSegment(nhBackgroundOperationStepStatusName(status))}`;
  }

  stepTitle(step: NhBackgroundOperationStep): string {
    return step.titleKey || step.stepKey;
  }

  percentage(value?: number): number {
    if (value === undefined || value === null || !Number.isFinite(value)) {
      return 0;
    }
    return Math.max(0, Math.min(100, value));
  }

  arguments(json?: string): Record<string, unknown> {
    if (!json) {
      return {};
    }
    try {
      const value = JSON.parse(json);
      return typeof value === 'object' && value !== null ? value : {};
    } catch {
      return {};
    }
  }

  hasBatch(step: NhBackgroundOperationStep): boolean {
    return step.discoveredItems > 0 || step.processedItems > 0 || step.activeItems > 0;
  }

  childLabel(child: NhBackgroundOperationChild): string {
    return child.fanOutItemKey || child.operationType;
  }

  canCancel(status: NhBackgroundOperationStatus): boolean {
    return !['Succeeded', 'Failed', 'Cancelled', 'TimedOut'].includes(nhBackgroundOperationStatusName(status));
  }

  canRetry(operation: NhBackgroundOperation): boolean {
    return !operation.sensitiveDataRedactedAt
      && ['Failed', 'Cancelled', 'TimedOut'].includes(nhBackgroundOperationStatusName(operation.status));
  }
}
