import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  ReactNode,
  useMemo,
} from "react";
import { User as FirebaseUser } from "firebase/auth";
import { auth, db } from "../config/firebase";
import { doc, getDoc, setDoc, Timestamp } from "firebase/firestore";

export interface AuthUser {
  uid: string;
  email: string | null;
  displayName: string | null;
  photoURL: string | null;
  role: "admin" | "user" | "initial";
}

interface AuthContextType {
  user: AuthUser | null;
  loading: boolean;
  isAuthenticated: boolean;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: ReactNode }> = ({
  children,
}) => {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);

  const resolveUser = async (firebaseUser: FirebaseUser) => {
    const userDocRef = doc(db, "users", firebaseUser.uid);
    const userDocSnap = await getDoc(userDocRef);

    if (userDocSnap.exists()) {
      const userData = userDocSnap.data();
      setUser({
        uid: firebaseUser.uid,
        email: firebaseUser.email,
        displayName: firebaseUser.displayName,
        photoURL: firebaseUser.photoURL,
        role: userData.role || "initial",
      });
    } else {
      // Primer login: crear documento en Firestore con rol por defecto
      await setDoc(userDocRef, {
        email: firebaseUser.email,
        name: firebaseUser.displayName || firebaseUser.email,
        photoURL: firebaseUser.photoURL || null,
        role: "initial",
        createdAt: Timestamp.now(),
        updatedAt: Timestamp.now(),
      });
      setUser({
        uid: firebaseUser.uid,
        email: firebaseUser.email,
        displayName: firebaseUser.displayName,
        photoURL: firebaseUser.photoURL,
        role: "initial",
      });
    }
  };

  useEffect(() => {
    const unsubscribe = auth.onAuthStateChanged(
      async (firebaseUser: FirebaseUser | null) => {
        try {
          if (firebaseUser) {
            await resolveUser(firebaseUser);
          } else {
            setUser(null);
          }
        } catch (error) {
          console.error("Error fetching user role:", error);
          setUser(null);
        } finally {
          setLoading(false);
        }
      },
    );

    return () => unsubscribe();
  }, []);

  const logout = async () => {
    try {
      await auth.signOut();
      setUser(null);
    } catch (error) {
      console.error("Error logging out:", error);
      throw error;
    }
  };

  const value = useMemo(
    () => ({
      user,
      loading,
      isAuthenticated: !!user,
      logout,
    }),
    [user, loading, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return context;
};
