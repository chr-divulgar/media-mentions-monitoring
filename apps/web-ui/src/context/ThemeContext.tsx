import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
} from "react";

export type ThemeMode = "dark" | "light";

interface ThemeContextType {
  themeMode: ThemeMode;
  setThemeMode: (mode: ThemeMode) => void;
  toggleThemeMode: () => void;
}

type ThemeAction = { type: "set"; mode: ThemeMode } | { type: "toggle" };

const STORAGE_KEY = "ui-theme-mode";

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

export const ThemeProvider: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const [themeMode, setThemeModeState] = useReducer(
    (currentMode: ThemeMode, action: ThemeAction) => {
      if (action.type === "toggle") {
        return currentMode === "dark" ? "light" : "dark";
      }

      return action.mode;
    },
    "dark" as ThemeMode,
    (initialMode) => {
      if (globalThis.window === undefined) {
        return initialMode;
      }

      const savedTheme = globalThis.localStorage.getItem(STORAGE_KEY);
      return savedTheme === "light" ? "light" : "dark";
    },
  );

  useEffect(() => {
    if (typeof document === "undefined") {
      return;
    }

    document.documentElement.dataset.theme = themeMode;
    document.body.dataset.theme = themeMode;
    globalThis.localStorage.setItem(STORAGE_KEY, themeMode);
  }, [themeMode]);

  const setThemeMode = useCallback((mode: ThemeMode) => {
    setThemeModeState({ type: "set", mode });
  }, []);

  const toggleThemeMode = useCallback(() => {
    setThemeModeState({ type: "toggle" });
  }, []);

  const value = useMemo(
    () => ({ themeMode, setThemeMode, toggleThemeMode }),
    [themeMode, setThemeMode, toggleThemeMode],
  );

  return (
    <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
  );
};

export const useThemeMode = () => {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error("useThemeMode must be used within a ThemeProvider");
  }

  return context;
};
