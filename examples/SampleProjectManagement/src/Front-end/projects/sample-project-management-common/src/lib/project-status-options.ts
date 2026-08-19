import {
  NhEnumDropDownItem,
  NhFormHelper
} from '@newheap/platform-common';
import { TranslateService } from '@ngx-translate/core';
import { ProjectStatus } from './project.models';

const PROJECT_STATUS_KEYS: Record<ProjectStatus, string> = {
  [ProjectStatus.Draft]: 'draft',
  [ProjectStatus.Active]: 'active',
  [ProjectStatus.OnHold]: 'on-hold',
  [ProjectStatus.Completed]: 'completed',
  [ProjectStatus.Archived]: 'archived'
};

export function projectStatusKey(status: ProjectStatus): string {
  return PROJECT_STATUS_KEYS[status];
}

export function projectStatusTranslationKey(status: ProjectStatus): string {
  return `project.status-${projectStatusKey(status)}`;
}

export function getProjectStatusOptions(
  translateService: TranslateService,
  emptyFirst = true,
  skipValues: ProjectStatus[] = []
): NhEnumDropDownItem<ProjectStatus | ''>[] {
  return NhFormHelper.getEnumDropDownByEnum(
    ProjectStatus,
    translateService,
    'project.status-',
    emptyFirst,
    skipValues,
    projectStatusTranslationKey
  );
}
