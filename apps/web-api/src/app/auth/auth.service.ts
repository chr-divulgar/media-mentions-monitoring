import { Injectable, Logger, UnauthorizedException } from '@nestjs/common';
import * as admin from 'firebase-admin';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';

// Firestore itself retries internally before surfacing an error (a quota exhaustion took ~10s to
// fail in practice), so a bare try/catch around the call still blocks the whole login for that
// long. Bounding the wait is what actually keeps the page responsive during a Firestore outage.
const FIRESTORE_TIMEOUT_MS = 3000;

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Firestore call timed out')), ms);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}

@Injectable()
export class AuthService {
  private readonly logger = new Logger(AuthService.name);

  // uid -> last role read from Firestore. It spares Firestore a read on every request (its quota
  // was a real problem) and gives a known-good answer during an outage instead of a guess.
  private readonly roleCache = new Map<string, { role: string; at: number }>();
  private static readonly ROLE_TTL_MS = 60_000;

  constructor(private readonly firebase: FirebaseAdminService) {}

  /** Verifies an ID token and resolves the caller's role; throws 401 on a bad token. */
  async authenticate(token: string): Promise<{ uid: string; role: string }> {
    const decoded = await this.verify(token);
    return { uid: decoded.uid, role: await this.resolveRole(decoded.uid) };
  }

  private async verify(token: string): Promise<admin.auth.DecodedIdToken> {
    try {
      // Token verification is Firebase Auth, not Firestore — unaffected by a Firestore outage.
      return await this.firebase.auth.verifyIdToken(token);
    } catch {
      throw new UnauthorizedException('Token inválido o expirado');
    }
  }

  /**
   * Role from Firestore (cached briefly). If Firestore is down the last known role is used; with
   * none known, 'initial' — least privilege, never 'admin'.
   */
  private async resolveRole(uid: string): Promise<string> {
    const cached = this.roleCache.get(uid);
    if (cached && Date.now() - cached.at < AuthService.ROLE_TTL_MS) return cached.role;

    try {
      const snap = await withTimeout(
        this.firebase.firestore.collection('users').doc(uid).get(),
        FIRESTORE_TIMEOUT_MS,
      );
      const role: string = snap.exists ? (snap.data()?.role ?? 'initial') : 'initial';
      this.roleCache.set(uid, { role, at: Date.now() });
      return role;
    } catch (error) {
      const fallback = cached?.role ?? 'initial';
      this.logger.warn(
        `Firestore role lookup failed for uid ${uid}, using '${fallback}': ${error instanceof Error ? error.message : JSON.stringify(error)}`,
      );
      return fallback;
    }
  }

  async getProfile(token: string) {
    const decoded = await this.verify(token);
    const uid = decoded.uid;

    // First login: create the user document with the default (unapproved) role. A Firestore
    // outage here must never block sign-in, and never grants more than the last known role.
    try {
      const docRef = this.firebase.firestore.collection('users').doc(uid);
      const snap = await withTimeout(docRef.get(), FIRESTORE_TIMEOUT_MS);

      if (!snap.exists) {
        await withTimeout(
          docRef.set({
            email: decoded.email ?? null,
            name: decoded.name ?? decoded.email ?? null,
            photoURL: decoded.picture ?? null,
            role: 'initial',
            createdAt: admin.firestore.Timestamp.now(),
            updatedAt: admin.firestore.Timestamp.now(),
          }),
          FIRESTORE_TIMEOUT_MS,
        );
      }
    } catch (error) {
      this.logger.warn(
        `Firestore profile check failed for uid ${uid}: ${error instanceof Error ? error.message : JSON.stringify(error)}`,
      );
    }

    // A profile fetch is how a changed role becomes visible, so skip the cached value here.
    this.roleCache.delete(uid);
    return { uid, role: await this.resolveRole(uid) };
  }
}
