import { Injectable, Logger, UnauthorizedException } from '@nestjs/common';
import * as admin from 'firebase-admin';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';
import { UsersBackupService } from '../config-db/users-backup.service';

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
  // After a failed Firestore read the answer in use (the backup copy's role, or the last known one)
  // is kept this long before Firestore is tried again, so an outage or an exhausted quota does not
  // cost every request its 3 s wait.
  private static readonly FAILURE_BACKOFF_MS = 5 * 60_000;

  constructor(
    private readonly firebase: FirebaseAdminService,
    private readonly usersBackup: UsersBackupService,
  ) {}

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
   * Role from Firestore (cached briefly). If Firestore is down the last known role is used, then
   * the role in the MongoDB backup copy of the users; with none known, 'initial' — least
   * privilege, never 'admin'.
   */
  private async resolveRole(uid: string): Promise<string> {
    const cached = this.roleCache.get(uid);
    if (cached && Date.now() - cached.at < AuthService.ROLE_TTL_MS) return this.effectiveRole(cached.role);

    try {
      const snap = await withTimeout(
        this.firebase.firestore.collection('users').doc(uid).get(),
        FIRESTORE_TIMEOUT_MS,
      );
      const role: string = snap.exists ? (snap.data()?.role ?? 'initial') : 'initial';
      this.roleCache.set(uid, { role, at: Date.now() });
      return this.effectiveRole(role);
    } catch (error) {
      const fallback = await this.fallbackRole(uid, cached?.role);
      this.logger.warn(
        `Firestore role lookup failed for uid ${uid}, using '${fallback}': ${error instanceof Error ? error.message : JSON.stringify(error)}`,
      );
      return this.effectiveRole(fallback);
    }
  }

  /** Last known role, else the backup copy's, else 'initial'; remembered so Firestore is not hammered. */
  private async fallbackRole(uid: string, lastKnown?: string): Promise<string> {
    const role = lastKnown ?? (await this.usersBackup.getRole(uid)) ?? 'initial';
    // Dated in the future so the entry stays valid for FAILURE_BACKOFF_MS instead of ROLE_TTL_MS.
    this.roleCache.set(uid, { role, at: Date.now() + AuthService.FAILURE_BACKOFF_MS - AuthService.ROLE_TTL_MS });
    return role;
  }

  /**
   * TEMPORARY: with INITIAL_AS_ADMIN=true a user without an approved role ('initial') gets the
   * admin screens and routes. Switched on only while Firestore was unreliable (quota); the stored
   * role is never changed, so removing the variable restores the real roles.
   */
  private effectiveRole(role: string): string {
    return process.env.INITIAL_AS_ADMIN === 'true' && role === 'initial' ? 'admin' : role;
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

      const data = snap.exists ? snap.data() ?? {} : {};
      const role: string = (data.role as string | undefined) ?? 'initial';
      this.roleCache.set(uid, { role, at: Date.now() });
      // Every login refreshes the backup copy from the source of truth (this also picks up a
      // role changed straight in the Firestore console).
      await this.usersBackup.upsert(uid, {
        email: (data.email as string | undefined) ?? decoded.email ?? null,
        name: (data.name as string | undefined) ?? decoded.name ?? decoded.email ?? null,
        photoURL: (data.photoURL as string | undefined) ?? decoded.picture ?? null,
        role,
        phone: data.phone as string | undefined,
        notifyPlatformErrors: data.notifyPlatformErrors as boolean | undefined,
      });
      return { uid, role: this.effectiveRole(role) };
    } catch (error) {
      this.logger.warn(
        `Firestore profile check failed for uid ${uid}: ${error instanceof Error ? error.message : JSON.stringify(error)}`,
      );
    }

    // Firestore could not answer: the backup copy (or the last known role) does.
    this.roleCache.delete(uid);
    return { uid, role: this.effectiveRole(await this.fallbackRole(uid)) };
  }
}
