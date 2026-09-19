import { Injectable, signal } from '@angular/core';

export interface Tokens {
  accessToken: string;
  refreshToken: string | null;
}

/**
 * Access and refresh tokens live in memory only — never localStorage/sessionStorage. A hard
 * page reload signs the user out; that's the accepted tradeoff for a bearer-token SPA with no
 * BFF cookie session (see the login page's design notes).
 */
@Injectable({ providedIn: 'root' })
export class TokenStore {
  private readonly tokens = signal<Tokens | null>(null);

  readonly isAuthenticated = signal(false);

  get accessToken(): string | null {
    return this.tokens()?.accessToken ?? null;
  }

  get refreshToken(): string | null {
    return this.tokens()?.refreshToken ?? null;
  }

  set(tokens: Tokens): void {
    this.tokens.set(tokens);
    this.isAuthenticated.set(true);
  }

  clear(): void {
    this.tokens.set(null);
    this.isAuthenticated.set(false);
  }
}
