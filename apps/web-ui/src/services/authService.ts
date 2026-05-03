import {
  signInWithPopup,
  GoogleAuthProvider,
  OAuthProvider,
} from "firebase/auth";
import { auth } from "../config/firebase";

const googleProvider = new GoogleAuthProvider();
const microsoftProvider = new OAuthProvider("microsoft.com");

googleProvider.addScope("profile");
googleProvider.addScope("email");

microsoftProvider.addScope("profile");
microsoftProvider.addScope("email");

/**
 * Login con Google
 */
export const signInWithGoogle = async () => {
  const result = await signInWithPopup(auth, googleProvider);
  return result.user;
};

/**
 * Login con Microsoft
 */
export const signInWithMicrosoft = async () => {
  const result = await signInWithPopup(auth, microsoftProvider);
  return result.user;
};
