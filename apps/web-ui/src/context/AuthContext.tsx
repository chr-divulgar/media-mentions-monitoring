import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  ReactNode,
  useMemo,
} from "react";
import { User as FirebaseUser } from "firebase/auth";
import { auth } from "../config/firebase";

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
    const idToken = await firebaseUser.getIdToken();
    const apiBase = import.meta.env.VITE_API_LOCAL ?? "";
    const response = await fetch(`${apiBase}/auth/profile`, {
      headers: { Authorization: `Bearer ${idToken}` },
    });
    if (!response.ok) throw new Error("Failed to fetch user profile");
    const { role } = await response.json();
    setUser({
      uid: firebaseUser.uid,
      email: firebaseUser.email,
      displayName: firebaseUser.displayName,
      photoURL: firebaseUser.photoURL,
      role: role ?? "initial",
    });
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
          setUser(
            firebaseUser
              ? {
                  uid: firebaseUser.uid,
                  email: firebaseUser.email,
                  displayName: firebaseUser.displayName,
                  photoURL: firebaseUser.photoURL,
                  role: "initial",
                }
              : null,
          );
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
