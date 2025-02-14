import {Inject, Injectable, Optional, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {DOCUMENT, isPlatformServer} from "@angular/common";
import {CookieService} from "ngx-cookie-service";
import {CookieOptions, SameSite} from "ngx-cookie-service/lib/cookie.service";
import {Cookie} from "tough-cookie";

@Injectable()
export class NhCookieService {
  public static readonly HTTP_COOKIE_PREFIX: string = '';
  public static readonly COOKIE_SPLIT_REGEX = /,(?=\s*\S+=)/;
  public static readonly NOP_COOKIE_CUSTOMER: string = '.Nop.Customer';
  public static readonly NOP_COOKIE_AUTHENTICATION: string = '.Nop.Authentication';
  public static readonly Nh_COOKIE_AUTHENTICATION: string = '.Nh.Authentication';

  constructor(
    @Inject(DOCUMENT) private document: Document,
    @Inject(PLATFORM_ID) private platformId: Object,
    @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any,
    private cookieService: CookieService,
  ) {
  }

  getAll(): string[] {
    let cookies: string[] = [];

    if(isPlatformServer(this.platformId)) {
      try {
        cookies = (<any>this?.requestContext?.request?.headers)['cookie']?.split(';').map((x: string) => x?.trim())
      } catch {
        cookies = this?.requestContext?.request?.headers.getSetCookie();
      }
    } else {
      cookies = this.document.cookie?.split(';').map((x: string) => x?.trim());
    }

    return cookies ?? [];
  }

  get(key: string): string|undefined {
    return this.getAll().find(x => x.startsWith(`${key}=`))?.split('=')[1] ?? undefined;
  }

  set(name: string, value: string, options?: CookieOptions) {
    this.cookieService.set(name, value, options);
  }

  delete(name: string, path?: CookieOptions['path'], domain?: CookieOptions['domain'], secure?: CookieOptions['secure'], sameSite?: SameSite) {
    this.cookieService.delete(name, path, domain, secure, sameSite);
  }



  public requestRawCookiesHeaderToCookies(rawCookiesHeader: string): Cookie[] {
    return rawCookiesHeader
      ?.split(';')
      ?.map(x => Cookie.parse(x?.trim()))
      .filter(x => x !== undefined)
      .map(x => <Cookie>x) ?? [];
  }

  public responseRawCookiesHeaderToCookies(rawCookiesHeader: string): Cookie[] {
    return rawCookiesHeader
      ?.split(NhCookieService.COOKIE_SPLIT_REGEX)
      ?.map(x => Cookie.parse(x?.trim()))
      .filter(x => x !== undefined)
      .map(x => <Cookie>x) ?? [];
  }

  public getRequestServerCookies() {
    let cookies: Cookie[] = [];

    if(isPlatformServer(this.platformId) && this?.requestContext?.request) {
      try {
        cookies = this.requestRawCookiesHeaderToCookies((<any>this?.requestContext?.request?.headers)['cookie']);
      } catch {
      }
    }

    return cookies ?? [];
  }

  public setServerCookies(cookies: Cookie[]) {
    if(!this?.requestContext?.response) {
      console.warn(`Skipped setting server cookies because response is not available.`);
      return;
    }

    for(const cookie of cookies) {
      cookie.key = `${NhCookieService.HTTP_COOKIE_PREFIX}${cookie.key}`;

      try {
        const responseAny = this.requestContext?.response as any;
        responseAny.appendHeader('Set-Cookie', cookie.toString());
      }catch (ex) {
        console.error('Failed to do Set-Cookie', ex);
      }
    }
  }
}
