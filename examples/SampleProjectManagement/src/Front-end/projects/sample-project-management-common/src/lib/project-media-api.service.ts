import { HttpClient, HttpHeaders, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export const SAMPLE_MEDIA_DIVISION_ID = '11111111-1111-1111-1111-111111111111';

export interface ProjectMediaFolderReference {
  id?: string;
  path?: string;
  name: string;
  fullPath: string;
}

export interface ProjectMediaFileReference {
  id: string;
  name: string;
  title?: string;
  description?: string;
  altText?: string;
  creator?: string;
  thumbnail?: string;
  metaData?: Record<string, unknown>;
  tags: string[];
  folder: ProjectMediaFolderReference;
  creationDateTime: string;
}

export interface ProjectMediaFolderContents {
  files: ProjectMediaFileReference[];
  folders: ProjectMediaFolderReference[];
}

export interface ProjectMediaSearchResults {
  results: ProjectMediaFileReference[];
  totalCount: number;
  itemsPerPage: number;
  pageIndex: number;
}

export interface ProjectMediaDiagnostics {
  mediaStorage: string;
  fileStructureStorage: string;
  authorizationModule: string;
  thumbnailService: string;
  eventHandlers: string[];
  contextValues: Record<string, string>;
  s3: {
    valid: boolean;
    bucketName: string;
    region: string;
    accessKey: string;
    secretKey: string;
    validationErrors: string[];
  };
  thumbnailCount: number;
  recentEvents: Array<{
    occurredAtUtc: string;
    resourceType: string;
    eventType: string;
    resourceId?: string;
    name: string;
  }>;
  recentAuthorizationDecisions: Array<{
    occurredAtUtc: string;
    divisionId?: string;
    action: string;
    path: string;
    requiredPermission: string;
    authorized: boolean;
    source: string;
  }>;
}

@Injectable({ providedIn: 'root' })
export class ProjectMediaApiService {
  readonly divisionId = SAMPLE_MEDIA_DIVISION_ID;
  readonly scopeRoot = `/divisions/${this.divisionId}/projects`;
  private readonly baseUrl = '/api/project-media';

  constructor(private readonly http: HttpClient) {}

  list(
    path = this.scopeRoot,
    language = 'nl',
    orderKey = 'Name',
    descending = false
  ): Observable<ProjectMediaFolderContents> {
    const orderBy = JSON.stringify([{
      key: orderKey,
      direction: descending ? 'Descending' : 'Ascending'
    }]);
    const params = new HttpParams()
      .set('path', path)
      .set('language', language)
      .set('page', 0)
      .set('pageSize', 50)
      .set('orderBy', orderBy);
    return this.http.get<ProjectMediaFolderContents>(`${this.baseUrl}/list`, {
      headers: this.headers(false),
      params
    });
  }

  search(searchTerm: string, path = this.scopeRoot): Observable<ProjectMediaSearchResults> {
    const params = new HttpParams()
      .set('path', path)
      .set('searchTerm', searchTerm)
      .set('language', 'nl')
      .set('pageIndex', 0)
      .set('pageSize', 20)
      .append('includeExtensions', 'pdf,txt,png,jpg,jpeg');
    return this.http.get<ProjectMediaSearchResults>(`${this.baseUrl}/search`, {
      headers: this.headers(false),
      params
    });
  }

  createFolder(path: string, folderName: string): Observable<ProjectMediaFolderReference> {
    return this.http.post<ProjectMediaFolderReference>(`${this.baseUrl}/folder`, null, {
      headers: this.headers(true),
      params: new HttpParams().set('path', path).set('folderName', folderName)
    });
  }

  renameFolder(folder: ProjectMediaFolderReference, newName: string): Observable<ProjectMediaFolderReference> {
    return this.http.put<ProjectMediaFolderReference>(`${this.baseUrl}/folder`, {
      path: folder.path,
      folderName: folder.name,
      newPath: folder.path,
      newName
    }, { headers: this.headers(true) });
  }

  deleteFolder(folder: ProjectMediaFolderReference): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/folder`, {
      headers: this.headers(true),
      params: new HttpParams()
        .set('path', folder.path ?? '/')
        .set('folderName', folder.name)
    });
  }

  upload(file: File, path: string, tags: string[]): Observable<ProjectMediaFileReference> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('fileName', file.name);
    form.append('path', path);
    form.append('title', file.name);
    form.append('description', 'Project document from the NewHeap media sample');
    form.append('creator', 'Sample Project Management');
    for (const tag of tags) form.append('tags', tag);
    return this.http.post<ProjectMediaFileReference>(`${this.baseUrl}/upload`, form, {
      headers: this.headers(true)
    });
  }

  download(file: ProjectMediaFileReference): Observable<HttpResponse<Blob>> {
    return this.http.get('/api/media-samples/download', {
      headers: this.headers(false),
      params: new HttpParams()
        .set('path', file.folder.fullPath)
        .set('fileName', file.name),
      observe: 'response',
      responseType: 'blob'
    });
  }

  updateTags(file: ProjectMediaFileReference, tags: string[]): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/file/tags`, {
      path: file.folder.fullPath,
      fileName: file.name,
      tags
    }, { headers: this.headers(true) });
  }

  localize(
    file: ProjectMediaFileReference,
    language: string,
    propertyName: 'title' | 'description' | 'altText',
    value: string
  ): Observable<void> {
    const params = new HttpParams()
      .set('path', file.folder.fullPath)
      .set('fileName', file.name)
      .set('language', language)
      .set('propertyName', propertyName)
      .set('value', value);
    return this.http.post<void>(`${this.baseUrl}/file/localize`, null, {
      headers: this.headers(true),
      params
    });
  }

  deleteFile(file: ProjectMediaFileReference): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/file`, {
      headers: this.headers(true),
      params: new HttpParams()
        .set('path', file.folder.fullPath)
        .set('fileName', file.name)
    });
  }

  diagnostics(): Observable<ProjectMediaDiagnostics> {
    return this.http.get<ProjectMediaDiagnostics>('/api/media-samples/diagnostics');
  }

  private headers(mutate: boolean): HttpHeaders {
    const permissions = mutate
      ? 'app.project.view,app.project.manage'
      : 'app.project.view';
    return new HttpHeaders({
      'X-NH-ActiveDivisionId': this.divisionId,
      'X-Sample-Media-Permissions': permissions
    });
  }
}
