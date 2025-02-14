import {Injectable} from '@angular/core';
import {Title} from '@angular/platform-browser';
import {BehaviorSubject} from 'rxjs';

@Injectable({
  providedIn: 'root',
})
export class NhTitleService {
  public title: BehaviorSubject<string>;

  constructor(private titleService: Title) {
    this.title = new BehaviorSubject<string>(titleService.getTitle());
  }

  setTitle(title: string) {
    this.titleService.setTitle(title);
    this.title.next(title);
  }
}
