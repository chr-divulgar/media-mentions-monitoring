import { Injectable, UnauthorizedException } from '@nestjs/common';
import * as admin from 'firebase-admin';
import { FirebaseAdminService } from '../firebase/firebase-admin.service';

@Injectable()
export class AuthService {
  constructor(private readonly firebase: FirebaseAdminService) {}

  async getProfile(token: string) {
    let decoded: admin.auth.DecodedIdToken;
    try {
      decoded = await this.firebase.auth.verifyIdToken(token);
    } catch {
      throw new UnauthorizedException('Token inválido o expirado');
    }

    const uid = decoded.uid;
    const docRef = this.firebase.firestore.collection('users').doc(uid);
    const snap = await docRef.get();

    if (snap.exists) {
      const data = snap.data();
      return { uid, role: data.role ?? 'initial' };
    }

    // Primer login: crear documento con rol por defecto
    await docRef.set({
      email: decoded.email ?? null,
      name: decoded.name ?? decoded.email ?? null,
      photoURL: decoded.picture ?? null,
      role: 'initial',
      createdAt: admin.firestore.Timestamp.now(),
      updatedAt: admin.firestore.Timestamp.now(),
    });

    return { uid, role: 'initial' };
  }
}
