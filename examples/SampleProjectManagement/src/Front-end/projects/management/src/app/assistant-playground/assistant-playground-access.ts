import { Injectable, inject } from '@angular/core';
import { NhAssistantAccessPolicy } from '@newheap/platform-ai-chat';
import { BehaviorSubject, Observable } from 'rxjs';

/** Playground switch that stands in for the user's assistant permission. */
@Injectable()
export class AssistantPlaygroundAccess {
  readonly granted = new BehaviorSubject(true);
}

/** Access policy of the playground scope, driven by the switch on the page. */
@Injectable()
export class AssistantPlaygroundAccessPolicy implements NhAssistantAccessPolicy {
  private readonly access = inject(AssistantPlaygroundAccess);

  canUse(): Observable<boolean> {
    return this.access.granted;
  }
}
