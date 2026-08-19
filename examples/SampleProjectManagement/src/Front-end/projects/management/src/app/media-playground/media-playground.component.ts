import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NhHttpUtil } from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import {
  ProjectMediaApiService,
  ProjectMediaDiagnostics,
  ProjectMediaFileReference,
  ProjectMediaFolderReference
} from 'sample-project-management-common';

@Component({
  selector: 'app-media-playground',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './media-playground.component.html',
  styleUrl: './media-playground.component.scss'
})
export class MediaPlaygroundComponent implements OnInit {
  private readonly mediaApi = inject(ProjectMediaApiService);

  readonly scopeRoot = this.mediaApi.scopeRoot;
  readonly currentPath = signal(this.scopeRoot);
  readonly folders = signal<ProjectMediaFolderReference[]>([]);
  readonly files = signal<ProjectMediaFileReference[]>([]);
  readonly selectedFile = signal<File | null>(null);
  readonly selectedReference = signal<ProjectMediaFileReference | null>(null);
  readonly diagnostics = signal<ProjectMediaDiagnostics | null>(null);
  readonly searchResults = signal<ProjectMediaFileReference[]>([]);
  readonly result = signal('Start the API through Aspire and run a media action.');
  readonly loading = signal(false);
  readonly newFolderName = signal('projectdocumenten');
  readonly tagsText = signal('project,document');
  readonly searchTerm = signal('');
  readonly localizationText = signal('Gelokaliseerde projecttitel');
  readonly language = signal<'nl' | 'en'>('en');
  readonly sortKey = signal<'Name' | 'CreationDateTime'>('Name');
  readonly descending = signal(false);

  readonly visibleFiles = computed(() =>
    this.searchTerm().trim() ? this.searchResults() : this.files());

  ngOnInit(): void {
    this.loadFolder();
    this.loadDiagnostics();
  }

  loadFolder(): void {
    this.loading.set(true);
    this.mediaApi.list(
      this.currentPath(),
      this.language(),
      this.sortKey(),
      this.descending()
    ).subscribe({
      next: contents => {
        this.folders.set(contents.folders);
        this.files.set(contents.files);
        this.searchResults.set([]);
        this.loading.set(false);
        this.result.set(`Loaded ${contents.folders.length} folders and ${contents.files.length} files.`);
      },
      error: error => this.fail(error)
    });
  }

  createFolder(): void {
    const name = this.newFolderName().trim();
    if (!name) return;
    this.loading.set(true);
    this.mediaApi.createFolder(this.currentPath(), name).subscribe({
      next: folder => {
        this.result.set(`Folder created: ${folder.fullPath}`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  openFolder(folder: ProjectMediaFolderReference): void {
    this.currentPath.set(folder.fullPath);
    this.loadFolder();
  }

  goToScopeRoot(): void {
    this.currentPath.set(this.scopeRoot);
    this.loadFolder();
  }

  renameFolder(folder: ProjectMediaFolderReference): void {
    const newName = window.prompt('New folder name', `${folder.name}-renamed`)?.trim();
    if (!newName) return;
    this.loading.set(true);
    this.mediaApi.renameFolder(folder, newName).subscribe({
      next: updated => {
        this.result.set(`Folder renamed to ${updated.fullPath}`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  deleteFolder(folder: ProjectMediaFolderReference): void {
    this.loading.set(true);
    this.mediaApi.deleteFolder(folder).subscribe({
      next: () => {
        this.result.set(`Folder and derived files deleted: ${folder.fullPath}`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  selectUpload(event: Event): void {
    this.selectedFile.set((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  upload(): void {
    const file = this.selectedFile();
    if (!file) {
      this.result.set('Choose a file first.');
      return;
    }
    this.loading.set(true);
    this.mediaApi.upload(file, this.currentPath(), this.parseTags()).subscribe({
      next: reference => {
        this.selectedReference.set(reference);
        this.result.set(`Upload gereed: ${reference.name} (${reference.id}).`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  download(file: ProjectMediaFileReference): void {
    this.mediaApi.download(file).subscribe({
      next: response => {
        const disposition = response.headers.get('content-disposition') ?? '';
        const filename = NhHttpUtil.filenameFromContentDisposition(disposition) || file.name;
        const href = URL.createObjectURL(response.body!);
        const anchor = document.createElement('a');
        anchor.href = href;
        anchor.download = filename;
        anchor.click();
        URL.revokeObjectURL(href);
        this.result.set(`Download: ${filename}, ${response.headers.get('content-type')}.`);
      },
      error: error => this.fail(error)
    });
  }

  updateTags(file: ProjectMediaFileReference): void {
    this.loading.set(true);
    this.mediaApi.updateTags(file, this.parseTags()).subscribe({
      next: () => {
        this.result.set(`Tags updated for ${file.name}.`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  localize(file: ProjectMediaFileReference): void {
    this.loading.set(true);
    this.mediaApi.localize(
      file,
      this.language() === 'nl' ? 'en' : 'nl',
      'title',
      this.localizationText()
    ).subscribe({
      next: () => {
        this.result.set(`Title translation saved for ${file.name}. Switch the list language to read it back.`);
        this.loadDiagnostics();
        this.loading.set(false);
      },
      error: error => this.fail(error)
    });
  }

  deleteFile(file: ProjectMediaFileReference): void {
    this.loading.set(true);
    this.mediaApi.deleteFile(file).subscribe({
      next: () => {
        this.selectedReference.set(null);
        this.result.set(`File and thumbnail deleted: ${file.name}.`);
        this.loadFolder();
        this.loadDiagnostics();
      },
      error: error => this.fail(error)
    });
  }

  search(): void {
    const term = this.searchTerm().trim();
    if (!term) {
      this.searchResults.set([]);
      return;
    }
    this.loading.set(true);
    this.mediaApi.search(term, this.scopeRoot).subscribe({
      next: response => {
        this.searchResults.set(response.results);
        this.loading.set(false);
        this.result.set(`${response.totalCount} mediaresultaten; pagina ${response.pageIndex + 1}.`);
      },
      error: error => this.fail(error)
    });
  }

  toggleLanguage(): void {
    this.language.update(language => language === 'nl' ? 'en' : 'nl');
    this.loadFolder();
  }

  toggleSort(): void {
    if (this.sortKey() === 'Name') {
      this.sortKey.set('CreationDateTime');
      this.descending.set(true);
    } else {
      this.sortKey.set('Name');
      this.descending.set(false);
    }
    this.loadFolder();
  }

  loadDiagnostics(): void {
    this.mediaApi.diagnostics().subscribe({
      next: diagnostics => this.diagnostics.set(diagnostics),
      error: error => this.fail(error)
    });
  }

  updateNewFolderName(event: Event): void {
    this.newFolderName.set((event.target as HTMLInputElement).value);
  }

  updateTagsText(event: Event): void {
    this.tagsText.set((event.target as HTMLInputElement).value);
  }

  updateSearchTerm(event: Event): void {
    this.searchTerm.set((event.target as HTMLInputElement).value);
  }

  updateLocalizationText(event: Event): void {
    this.localizationText.set((event.target as HTMLInputElement).value);
  }

  private parseTags(): string[] {
    return this.tagsText()
      .split(',')
      .map(tag => tag.trim().toLowerCase())
      .filter(Boolean);
  }

  private fail(error: any): void {
    this.loading.set(false);
    const details = error?.error ? JSON.stringify(error.error) : error?.message;
    this.result.set(`Media action failed (${error?.status ?? 'offline'}): ${details ?? 'start the API through Aspire'}`);
  }
}
