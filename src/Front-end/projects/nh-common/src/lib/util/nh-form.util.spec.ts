import {TranslateService} from '@ngx-translate/core';
import {NhFormHelper} from './nh-form.util';

enum ProjectStatus {
  Draft = 'Draft',
  OnHold = 'OnHold',
  Archived = 'Archived'
}

describe('NhFormHelper', () => {
  it('builds translated enum options with skips and a custom translation key', () => {
    const translateService = {
      instant: jasmine.createSpy('instant').and.callFake((key: string) => `translated:${key}`)
    } as unknown as TranslateService;

    const options = NhFormHelper.getEnumDropDownByEnum<ProjectStatus>(
      ProjectStatus,
      translateService,
      'project.status-',
      true,
      [ProjectStatus.Archived],
      value => `project.status-${value === ProjectStatus.OnHold ? 'on-hold' : value.toLowerCase()}`
    );

    expect(options).toEqual([
      {id: '', name: 'translated:general.make-a-choice'},
      {id: ProjectStatus.Draft, name: 'translated:project.status-draft'},
      {id: ProjectStatus.OnHold, name: 'translated:project.status-on-hold'}
    ]);
  });
});
