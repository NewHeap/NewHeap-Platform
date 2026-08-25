import { CollectionHttpRequestOptions } from './http.models';

export type NhBackgroundOperationStatus =
  | 'PendingDispatch'
  | 'Queued'
  | 'Running'
  | 'WaitingForChildren'
  | 'CancelRequested'
  | 'RetryScheduled'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'TimedOut'
  | number;

export type NhBackgroundOperationAttemptStatus =
  | 'Queued'
  | 'Running'
  | 'Suspended'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'Abandoned'
  | number;

export type NhBackgroundOperationStepStatus =
  | 'Pending'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Skipped'
  | 'Cancelled'
  | 'Indeterminate'
  | number;

export type NhBackgroundOperationAggregationMode =
  | 'Manual'
  | 'WeightedChildren'
  | 'ItemCount'
  | 'Indeterminate'
  | 'ChildOperations'
  | number;

export type NhBackgroundOperationEventType =
  | 'StateChanged'
  | 'StepStarted'
  | 'StepProgressed'
  | 'StepCompleted'
  | 'BatchChanged'
  | 'RetryScheduled'
  | 'CancellationRequested'
  | 'Message'
  | 'CheckpointChanged'
  | 'ResultAvailable'
  | 'ChildrenCreated'
  | 'ChildrenCompleted'
  | number;

export type NhBackgroundOperationMessageSeverity =
  | 'Information'
  | 'Success'
  | 'Warning'
  | 'Error'
  | number;

export interface NhBackgroundOperationAttempt {
  id: string;
  attemptNumber: number;
  status: NhBackgroundOperationAttemptStatus;
  startedAt?: string;
  heartbeatAt?: string;
  completedAt?: string;
  failureCode?: string;
  recoveryReason?: string;
}

export interface NhBackgroundOperationStep {
  id: string;
  parentStepId?: string;
  stepKey: string;
  titleKey?: string;
  titleArgumentsJson?: string;
  messageKey?: string;
  messageArgumentsJson?: string;
  status: NhBackgroundOperationStepStatus;
  aggregationMode: NhBackgroundOperationAggregationMode;
  weight: number;
  current?: number;
  total?: number;
  percentage?: number;
  discoveredItems: number;
  processedItems: number;
  succeededItems: number;
  failedItems: number;
  skippedItems: number;
  retriedItems: number;
  activeItems: number;
  continueOnChildFailure: boolean;
  displayOrder: number;
  depth: number;
  startedAt?: string;
  completedAt?: string;
  children: NhBackgroundOperationStep[];
}

export interface NhBackgroundOperationEvent {
  id: string;
  sequence: number;
  stepId?: string;
  stepKey?: string;
  eventType: NhBackgroundOperationEventType;
  severity: NhBackgroundOperationMessageSeverity;
  messageKey?: string;
  messageArgumentsJson?: string;
  snapshotVersion: number;
  creationDateTime: string;
  resultReferenceType?: string;
  resultReferenceId?: string;
  resultUrl?: string;
  isMilestone: boolean;
}

export interface NhBackgroundOperation {
  id: string;
  creationDateTime: string;
  lastModifiedDateTime: string;
  operationType: string;
  payloadSchemaVersion: number;
  ownerUserId: string;
  divisionId?: string;
  parentOperationId?: string;
  rootOperationId?: string;
  fanOutKey?: string;
  fanOutItemKey?: string;
  status: NhBackgroundOperationStatus;
  queue: string;
  priority: number;
  currentAttemptNumber: number;
  domainObjectType?: string;
  domainObjectId?: string;
  correlationId?: string;
  progressCurrent?: number;
  progressTotal?: number;
  progressPercentage?: number;
  progressPhaseKey?: string;
  progressMessageKey?: string;
  progressMessageArgumentsJson?: string;
  cancelRequestedAt?: string;
  startedAt?: string;
  heartbeatAt?: string;
  completedAt?: string;
  sensitiveDataRedactedAt?: string;
  resultReferenceType?: string;
  resultReferenceId?: string;
  resultUrl?: string;
  failureCode?: string;
  failureMessageKey?: string;
  version: number;
  latestEventSequence: number;
  attempts: NhBackgroundOperationAttempt[];
  steps: NhBackgroundOperationStep[];
  events: NhBackgroundOperationEvent[];
  children: NhBackgroundOperationChild[];
}

export interface NhBackgroundOperationChild {
  id: string;
  parentOperationId?: string;
  operationType: string;
  fanOutKey?: string;
  fanOutItemKey?: string;
  status: NhBackgroundOperationStatus;
  progressPercentage?: number;
  creationDateTime: string;
  lastModifiedDateTime: string;
  completedAt?: string;
  resultReferenceType?: string;
  resultReferenceId?: string;
  resultUrl?: string;
  failureCode?: string;
  children: NhBackgroundOperationChild[];
}

export interface NhBackgroundOperationChanged {
  operationId: string;
  version: number;
  latestEventSequence: number;
  status: NhBackgroundOperationStatus;
  divisionId?: string;
}

export class NhBackgroundOperationCollectionHttpRequestOptions extends CollectionHttpRequestOptions {
  public constructor(init?: Partial<NhBackgroundOperationCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export function nhBackgroundOperationStatusName(status: NhBackgroundOperationStatus): string {
  if (typeof status === 'string') {
    return status;
  }

  return ({
    0: 'PendingDispatch',
    10: 'Queued',
    20: 'Running',
    25: 'WaitingForChildren',
    30: 'CancelRequested',
    40: 'RetryScheduled',
    100: 'Succeeded',
    110: 'Failed',
    120: 'Cancelled',
    130: 'TimedOut'
  } as Record<number, string>)[status] ?? 'Unknown';
}

export function nhBackgroundOperationStepStatusName(status: NhBackgroundOperationStepStatus): string {
  if (typeof status === 'string') {
    return status;
  }

  return ({
    0: 'Pending',
    10: 'Running',
    100: 'Succeeded',
    110: 'Failed',
    120: 'Skipped',
    130: 'Cancelled',
    140: 'Indeterminate'
  } as Record<number, string>)[status] ?? 'Unknown';
}

export function nhBackgroundOperationTranslationSegment(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/[_\s]+/g, '-')
    .toLowerCase();
}
