import ReactDOM from "react-dom/client";

import { ConfigProvider, App as AntApp, theme } from "antd";
import esES from "antd/locale/es_ES";
import { QueryClient, QueryClientProvider } from "react-query";
import App from "./app/app";
import { AlertProvider } from "./pages/alerts/AlertsContext";
import { NoteProvider } from "./pages/notes/NoteContext";
import { AuthProvider } from "./context/AuthContext";
import { ThemeProvider, useThemeMode } from "./context/ThemeContext";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      staleTime: Infinity,
    },
  },
});

const ThemedApp = () => {
  const { themeMode } = useThemeMode();
  const isDarkMode = themeMode === "dark";

  return (
    <ConfigProvider
      getTargetContainer={() => {
        return document.getElementById("test-pro-layout") ?? document.body;
      }}
      locale={esES}
      theme={{
        algorithm: isDarkMode ? theme.darkAlgorithm : theme.defaultAlgorithm,
        token: {
          colorPrimary: "#1677ff",
          borderRadius: 10,
           colorBgBase: isDarkMode ? "#030303" : "#ffffff",
           colorBgContainer: isDarkMode ? "#141414" : "#ffffff",
        },
         components: {
           Menu: {
             darkItemBg: "#141414",
           },
         },
      }}
    >
      <AntApp>
        <AlertProvider>
          <NoteProvider>
            <App />
          </NoteProvider>
        </AlertProvider>
      </AntApp>
    </ConfigProvider>
  );
};

ReactDOM.createRoot(document.getElementById("root")!).render(
  <div
    id="test-pro-layout"
    style={{
      height: "100vh",
      overflow: "auto",
    }}
  >
    <ThemeProvider>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <ThemedApp />
        </QueryClientProvider>
      </AuthProvider>
    </ThemeProvider>
  </div>,
);
