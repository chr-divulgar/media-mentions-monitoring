import type { ProSettings } from "@ant-design/pro-components";
import { ProLayout } from "@ant-design/pro-components";
import { useState } from "react";
import {
  HashRouter as Router,
  Routes,
  Route,
  Navigate,
  useNavigate,
} from "react-router-dom";
import { Spin, Layout, App as AntApp } from "antd";
import RouteCard from "./RouteCardComponent";
import defaultProps from "./_defaultProps";
import {
  headerTitleRender,
  menuFooterRender,
  menuItemRender,
  getActionsRender,
  getAvatarProps,
} from "./appHelpers";
import "./app.module.css";
import Alerts from "../pages/alerts/Alerts";
import NotesPage from "../pages/notes/NotesPage";
import DashboardPage from "../pages/dashboard";
import SettingsPage from "../pages/settings/SettingsPage";
import LoginPage from "../pages/auth/LoginPage";
import PrivateRoute from "../components/PrivateRoute";
import { useAuth } from "../context/AuthContext";
import { useThemeMode } from "../context/ThemeContext";

// Componente que usa hooks de router — debe estar DENTRO del <Router>
function AppLayout() {
  const [pathname, setPathname] = useState("/dashboard");
  const { isAuthenticated, loading, user, logout } = useAuth();
  const { themeMode, setThemeMode } = useThemeMode();
  const navigate = useNavigate();
  const { message } = AntApp.useApp();
  const isDarkMode = themeMode === "dark";

  // handler de logout definido aquí para que los hooks siempre se ejecuten en el mismo orden
  const handleLogout = async () => {
    try {
      await logout();
      message.success("Logged out successfully");
      navigate("/login");
    } catch {
      message.error("Failed to logout");
    }
  };

  const avatarProps = getAvatarProps({
    user,
    onLogout: handleLogout,
    themeMode,
    onThemeChange: setThemeMode,
  });

  const settings: Partial<ProSettings> = {
    fixSiderbar: true,
    layout: "mix",
    splitMenus: false,
    navTheme: isDarkMode ? "realDark" : "light",
    siderMenuType: "sub",
    fixedHeader: false,
  };

  // Spinner mientras verifica la sesión
  if (loading) {
    return (
      <Layout
        style={{
          minHeight: "100vh",
          display: "flex",
          justifyContent: "center",
          alignItems: "center",
          background: isDarkMode ? "#030303" : "#f5f7fa",
        }}
      >
        <Spin size="large" />
      </Layout>
    );
  }

  // Sin autenticar: redirigir a login
  if (!isAuthenticated) {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  // Filtrar menú según rol
  let menuItems = defaultProps.route.routes;
  if (user?.role === "user") {
    menuItems = defaultProps.route.routes.filter(
      (item) => item.path === "/dashboard" || item.path === "/notes",
    );
  }

  const filteredProps = {
    ...defaultProps,
    route: {
      ...defaultProps.route,
      routes: menuItems,
    },
  };

  return (
    <ProLayout
      {...filteredProps}
      location={{ pathname }}
      token={{
        bgLayout: isDarkMode ? "#030303" : "#f5f7fa",
        colorTextAppListIcon: isDarkMode ? "rgba(255,255,255,0.85)" : undefined,
        header: {
          colorBgMenuItemSelected: isDarkMode
            ? "rgba(255,255,255,0.12)"
            : "rgba(0,0,0,0.04)",
        },
      }}
      contentStyle={{
        background: isDarkMode ? "#030303" : "#f5f7fa",
        transition: "background-color 0.25s ease",
      }}
      menu={{ collapsedShowGroupTitle: true }}
      avatarProps={avatarProps}
      actionsRender={(props) => getActionsRender(props)}
      headerTitleRender={headerTitleRender}
      menuFooterRender={menuFooterRender}
      onMenuHeaderClick={(e) => console.log(e)}
      menuItemRender={(item, dom) => menuItemRender(item, dom, setPathname)}
      {...settings}
    >
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          path="/dashboard"
          element={
            <PrivateRoute>
              <RouteCard>
                <DashboardPage />
              </RouteCard>
            </PrivateRoute>
          }
        />
        <Route
          path="/notes"
          element={
            <PrivateRoute>
              <RouteCard>
                <NotesPage />
              </RouteCard>
            </PrivateRoute>
          }
        />
        <Route
          path="/alerts"
          element={
            <PrivateRoute requiredRole="admin">
              <RouteCard>
                <Alerts />
              </RouteCard>
            </PrivateRoute>
          }
        />
        <Route
          path="/admin/sub-page1"
          element={
            <PrivateRoute requiredRole="admin">
              <RouteCard>
                <div>AQUI DOS</div>
              </RouteCard>
            </PrivateRoute>
          }
        />
        <Route
          path="/settings"
          element={
            <PrivateRoute requiredRole="admin">
              <RouteCard>
                <SettingsPage />
              </RouteCard>
            </PrivateRoute>
          }
        />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Routes>
    </ProLayout>
  );
}

function App() {
  if (typeof document === "undefined") {
    return <div />;
  }

  return (
    <Router>
      <AppLayout />
    </Router>
  );
}

export default App;
