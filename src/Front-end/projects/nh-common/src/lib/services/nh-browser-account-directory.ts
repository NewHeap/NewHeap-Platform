import {Base64} from 'js-base64';

import {INhAuthorization} from '../models/auth.models';

/**
 * Internal storage of the opt-in account directory of `BaseNhAuthService`. Not exported from the
 * package: consumers use `BaseNhAuthService.accounts` and its account methods.
 */

/** The `localStorage` key of the account directory. `at` keeps holding the active authorization. */
export const nhBrowserAccountDirectoryStorageKey = 'nh-accounts';

/** One signed-in account. Unlike `NhBrowserAccount` it holds the account's authorization and tokens. */
export interface NhStoredBrowserAccount<TAuthorization extends INhAuthorization> {
  userId: string;
  userName: string;
  displayName?: string;
  signedInAt: string;
  lastUsedAt: string;
  authorization: TAuthorization;
}

interface NhStoredBrowserAccountDirectory<TAuthorization extends INhAuthorization> {
  version: 1;
  accounts: NhStoredBrowserAccount<TAuthorization>[];
}

const storageLeasePrefix = 'nh-lock:';
const storageLeaseMilliseconds = 15_000;
const storageLeaseRetryMilliseconds = 50;
const jwtUserIdClaimTypes = [
  'nameid',
  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier',
  'sub'
];

/**
 * Reads the directory and leaves out accounts that can no longer be renewed: without a user id,
 * user name or refresh token, or whose refresh token expired. An unreadable directory reads as
 * empty, as an unreadable `at` does. `pruned` tells the caller to write the result back.
 */
export function nhReadBrowserAccountDirectory<TAuthorization extends INhAuthorization>(
  now: number): {accounts: NhStoredBrowserAccount<TAuthorization>[], pruned: boolean} {
  let stored: string | null;

  try {
    stored = localStorage.getItem(nhBrowserAccountDirectoryStorageKey);
  } catch {
    // Without storage, as during server rendering, the browser holds no accounts.
    return {accounts: [], pruned: false};
  }

  if (!stored) {
    return {accounts: [], pruned: false};
  }

  let directory: NhStoredBrowserAccountDirectory<TAuthorization> | undefined;
  try {
    directory = JSON.parse(Base64.decode(stored));
  } catch {
    directory = undefined;
  }

  if (directory?.version !== 1 || !Array.isArray(directory.accounts)) {
    return {accounts: [], pruned: true};
  }

  const accounts = new Array<NhStoredBrowserAccount<TAuthorization>>();
  for (const account of directory.accounts) {
    if (!nhIsRenewableBrowserAccount(account, now)) {
      continue;
    }

    const duplicate = accounts.findIndex(existing => existing.userId === account.userId);
    if (duplicate < 0) {
      accounts.push(account);
    } else if (account.lastUsedAt > accounts[duplicate].lastUsedAt) {
      accounts[duplicate] = account;
    }
  }

  return {accounts: accounts, pruned: accounts.length !== directory.accounts.length};
}

/** Writes the directory, or removes it when no account is left. */
export function nhWriteBrowserAccountDirectory<TAuthorization extends INhAuthorization>(
  accounts: NhStoredBrowserAccount<TAuthorization>[]): void {
  try {
    if (accounts.length === 0) {
      localStorage.removeItem(nhBrowserAccountDirectoryStorageKey);
      return;
    }

    const directory: NhStoredBrowserAccountDirectory<TAuthorization> = {version: 1, accounts: accounts};
    localStorage.setItem(nhBrowserAccountDirectoryStorageKey, Base64.encode(JSON.stringify(directory)));
  } catch {
    // Without storage the browser cannot keep more than the session in memory.
  }
}

/**
 * The refresh-token expiry of an authorization. A stored sign-in response names it
 * `refreshValidTo`, a renewed session `refreshTokenExpires`.
 */
export function nhRefreshTokenExpiry(authorization: INhAuthorization | undefined): string | undefined {
  const response = authorization as (INhAuthorization & {refreshValidTo?: string | null}) | undefined;
  const expiry = response?.refreshTokenExpires ?? response?.refreshValidTo ?? undefined;

  return typeof expiry === 'string' ? expiry : undefined;
}

export function nhHasExpired(value: string | undefined, now: number): boolean {
  if (typeof value !== 'string' || value.length === 0) {
    return false;
  }

  const expiresAt = Date.parse(value);
  return !Number.isNaN(expiresAt) && expiresAt <= now;
}

/**
 * Reads the user id from the payload of a JWT access token, without validating it. The result only
 * finds the account's entry in this browser; the server decides who is signed in.
 */
export function nhAccessTokenUserId(token: unknown): string | undefined {
  if (typeof token !== 'string') {
    return undefined;
  }

  const payload = token.split('.')[1];
  if (!payload) {
    return undefined;
  }

  let claims: Record<string, unknown>;
  try {
    claims = JSON.parse(Base64.decode(payload));
  } catch {
    return undefined;
  }

  for (const claimType of jwtUserIdClaimTypes) {
    const value = claims?.[claimType];
    if (typeof value === 'string' && value.trim().length > 0) {
      return value;
    }
  }

  return undefined;
}

/**
 * Runs `task` while this tab holds a `localStorage` lease named `name`, for browsers without the
 * Web Locks API. The lease expires after 15 seconds, so a tab that closed while holding it blocks
 * the others no longer than that. Without storage the task runs uncoordinated.
 */
export async function nhRunWithStorageLease<T>(name: string, task: () => Promise<T>): Promise<T> {
  const key = storageLeasePrefix + name;
  const owner = createLeaseOwner();
  const acquired = await acquireStorageLease(key, owner);

  // Extends the lease while the task runs, so a slow task keeps it and a closed tab loses it soon.
  const extension = acquired
    ? setInterval(() => extendStorageLease(key, owner), storageLeaseMilliseconds / 3)
    : undefined;

  try {
    return await task();
  } finally {
    if (extension !== undefined) {
      clearInterval(extension);
    }

    if (acquired) {
      releaseStorageLease(key, owner);
    }
  }
}

function nhIsRenewableBrowserAccount<TAuthorization extends INhAuthorization>(
  account: NhStoredBrowserAccount<TAuthorization> | undefined,
  now: number): account is NhStoredBrowserAccount<TAuthorization> {
  if (!account || typeof account.userId !== 'string' || account.userId.length === 0) {
    return false;
  }

  if (typeof account.userName !== 'string' || account.userName.length === 0) {
    return false;
  }

  // Storage can be changed outside this library; an entry of the wrong shape is dropped, not trusted.
  if (typeof account.signedInAt !== 'string' || typeof account.lastUsedAt !== 'string') {
    return false;
  }

  if (account.displayName !== undefined && typeof account.displayName !== 'string') {
    return false;
  }

  if (typeof account.authorization !== 'object' || account.authorization === null) {
    return false;
  }

  if (typeof account.authorization.refreshToken !== 'string' || account.authorization.refreshToken.length === 0) {
    return false;
  }

  return !nhHasExpired(nhRefreshTokenExpiry(account.authorization), now);
}

async function acquireStorageLease(key: string, owner: string): Promise<boolean> {
  for (;;) {
    let lease: {owner?: string, expiresAt?: number} | null;

    try {
      lease = JSON.parse(localStorage.getItem(key) ?? 'null');
    } catch {
      lease = null;
    }

    // A lease further ahead than one term was not written by a tab that holds it now: a clock that
    // moved back or changed storage must not block every renewal.
    const now = Date.now();
    const held = !!lease
      && typeof lease.expiresAt === 'number'
      && lease.expiresAt > now
      && lease.expiresAt <= now + storageLeaseMilliseconds;

    if (!held) {
      try {
        localStorage.setItem(key, JSON.stringify({owner: owner, expiresAt: Date.now() + storageLeaseMilliseconds}));
      } catch {
        return false;
      }

      // Another tab may have written its lease at the same moment; the last writer holds it.
      await delay(storageLeaseRetryMilliseconds / 2);

      if (readStorageLeaseOwner(key) === owner) {
        return true;
      }
    }

    await delay(storageLeaseRetryMilliseconds);
  }
}

function extendStorageLease(key: string, owner: string): void {
  try {
    if (readStorageLeaseOwner(key) === owner) {
      localStorage.setItem(key, JSON.stringify({owner: owner, expiresAt: Date.now() + storageLeaseMilliseconds}));
    }
  } catch {
    // Without storage the lease cannot be extended; it expires on its own.
  }
}

function releaseStorageLease(key: string, owner: string): void {
  try {
    if (readStorageLeaseOwner(key) === owner) {
      localStorage.removeItem(key);
    }
  } catch {
    // The lease expires on its own.
  }
}

function readStorageLeaseOwner(key: string): string | undefined {
  try {
    return JSON.parse(localStorage.getItem(key) ?? 'null')?.owner;
  } catch {
    return undefined;
  }
}

function createLeaseOwner(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `${Date.now()}-${Math.random()}`;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
