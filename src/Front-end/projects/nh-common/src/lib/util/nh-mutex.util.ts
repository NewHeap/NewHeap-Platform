export class NhMutex {
  private lockPromise: Promise<void> = Promise.resolve();

  async lock(): Promise<() => void> {
    let release: () => void;
    const newLock = new Promise<void>((resolve) => (release = resolve));

    const currentLock = this.lockPromise;
    this.lockPromise = this.lockPromise.then(() => newLock);

    await currentLock;
    return release!;
  }
}

export class NhAsyncLock {
  private isLocked = false;

  async runExclusive<T>(fn: () => Promise<T>): Promise<T> {
    while (this.isLocked) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    this.isLocked = true;
    try {
      return await fn();
    } finally {
      this.isLocked = false;
    }
  }
}
