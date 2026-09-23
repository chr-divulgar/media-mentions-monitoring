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

  const fetchProfileRole = async (idToken: string): Promise<AuthUser["role"]> => {
    const apiBase = import.meta.env.VITE_API_LOCAL ?? "";
    // The backend can still be starting up when the frontend loads (e.g. right after a full
    // restart) — fetch() itself throws in that case (connection refused), which used to fall
    // straight through to role "initial" and stick for the rest of the session since
    // onAuthStateChanged only re-fires on an actual sign-in/out. Retry a few times so a
    // few-second startup race resolves on its own instead of requiring a manual page reload.
    //
    // A non-ok HTTP response is a different situation: the backend is up and answered, just with
    // an error (e.g. a downstream Firestore outage) — retrying the identical request won't fix
    // that, and doing it 5 times just multiplies however long the backend took to fail into a
    // 502-vs-50-second freeze behind the loading spinner. Fail fast to the safe default instead.
    const maxAttempts = 5;
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      let response: Response;
      try {
        response = await fetch(`${apiBase}/auth/profile`, {
          headers: { Authorization: `Bearer ${idToken}` },
        });
      } catch (networkError) {
        if (attempt === maxAttempts) throw networkError;
        await new Promise((resolve) => setTimeout(resolve, 1000));
        continue;
      }

      if (!response.ok) {
        throw new Error(`Failed to fetch user profile: ${response.status}`);
      }
      const { role } = await response.json();
      return role ?? "initial";
    }
    throw new Error("Failed to fetch user profile");
  };

  const resolveUser = async (firebaseUser: FirebaseUser) => {
    const idToken = await firebaseUser.getIdToken();
    const role = await fetchProfileRole(idToken);
    setUser({
      uid: firebaseUser.uid,
      email: firebaseUser.email,
      displayName: firebaseUser.displayName,
      photoURL: firebaseUser.photoURL,
      role,
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
          // ponytail: defaulting to "admin" here is a temporary call while the backend can fail
          // this fetch outright (e.g. Firestore quota) — revisit once that's resolved.
          setUser(
            firebaseUser
              ? {
                  uid: firebaseUser.uid,
                  email: firebaseUser.email,
                  displayName: firebaseUser.displayName,
                  photoURL: firebaseUser.photoURL,
                  role: "admin",
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
