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

  constructor(private readonly firebase: FirebaseAdminService) {}

  async getProfile(token: string) {
    let decoded: admin.auth.DecodedIdToken;
    try {
      // Token verification is Firebase Auth, not Firestore — unaffected by a Firestore outage.
      decoded = await this.firebase.auth.verifyIdToken(token);
    } catch {
      throw new UnauthorizedException('Token inválido o expirado');
    }

    const uid = decoded.uid;

    // The role lookup is the only Firestore-dependent part of login. Its failure (e.g. quota
    // exhaustion) must never block sign-in — degrade to a default role instead of throwing, so
    // the rest of the app (Mongo-backed alerts included) keeps working.
    // ponytail: defaulting to 'admin' during an outage is a temporary call while Firestore quota
    // is the active problem, not a permanent policy — revisit once quota is sorted (e.g. default
    // to 'initial' again, or cache each uid's last-known role to fall back to instead of a fixed one).
    try {
      const docRef = this.firebase.firestore.collection('users').doc(uid);
      const snap = await withTimeout(docRef.get(), FIRESTORE_TIMEOUT_MS);

      if (snap.exists) {
        const data = snap.data();
        return { uid, role: data.role ?? 'initial' };
      }

      // Primer login: crear documento con rol por defecto
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

      return { uid, role: 'initial' };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.logger.warn(
        `Firestore role lookup failed for uid ${uid}, falling back to 'admin': ${message}`,
      );
      return { uid, role: 'admin' };
    }
  }
}
