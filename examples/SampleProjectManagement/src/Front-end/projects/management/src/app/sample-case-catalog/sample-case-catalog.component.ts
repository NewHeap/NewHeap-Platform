import { CommonModule } from '@angular/common';
import { Component, computed, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { SAMPLE_CASES, SampleCase } from 'sample-project-management-common';

@Component({
  selector: 'app-sample-case-catalog',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './sample-case-catalog.component.html',
  styleUrl: './sample-case-catalog.component.scss'
})
export class SampleCaseCatalogComponent {
  readonly cases = SAMPLE_CASES;
  readonly query = signal('');
  readonly category = signal('');
  readonly implementation = signal('');
  readonly selectedCase = signal<SampleCase | undefined>(undefined);

  readonly categories = [...new Set(SAMPLE_CASES.map(item => item.category))];
  readonly implementedCount = SAMPLE_CASES.filter(
    item => item.implementation === 'implemented'
  ).length;
  readonly partialCount = SAMPLE_CASES.filter(
    item => item.implementation === 'partial'
  ).length;
  readonly gapCount = SAMPLE_CASES.filter(
    item => item.implementation === 'library-gap'
  ).length;

  readonly filteredCases = computed(() => {
    const query = this.query().trim().toLowerCase();
    const category = this.category();
    const implementation = this.implementation();

    return SAMPLE_CASES.filter(item => {
      const matchesText = !query ||
        `${item.id} ${item.title} ${item.surface} ${item.outcome}`
          .toLowerCase()
          .includes(query);
      const matchesCategory = !category || item.category === category;
      const matchesImplementation = !implementation ||
        item.implementation === implementation;

      return matchesText && matchesCategory && matchesImplementation;
    });
  });

  updateQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  updateCategory(event: Event): void {
    this.category.set((event.target as HTMLSelectElement).value);
  }

  updateImplementation(event: Event): void {
    this.implementation.set((event.target as HTMLSelectElement).value);
  }

  select(item: SampleCase): void {
    this.selectedCase.set(item);
  }

  anchorFor(item: SampleCase): string | undefined {
    if (item.implementation !== 'implemented') return undefined;
    const id = Number(item.id.slice(4));
    if (id <= 15 || (id >= 36 && id <= 60) || (id >= 114 && id <= 125)) return '#projects';
    if (id >= 16 && id <= 35) return '#collection-playground';
    if (id >= 61 && id <= 75) return '#auth-playground';
    if (id >= 76 && id <= 90) return '#notification-playground';
    if ((id >= 91 && id <= 113) || (id >= 131 && id <= 140) || (id >= 151 && id <= 161)) return '#platform-playground';
    if (id >= 126 && id <= 130) return '#collection-playground';
    if (id >= 141 && id <= 150) return '#utility-playground';
    return '#top';
  }
}
