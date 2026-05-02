import type { ProSettings } from "@ant-design/pro-components";
import { ProLayout } from "@ant-design/pro-components";
import { useState } from "react";
import { HashRouter as Router, Routes, Route } from "react-router-dom";
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

function App() {
  const [pathname, setPathname] = useState("/admin/sub-page1");

  const settings: Partial<ProSettings> = {
    fixSiderbar: true,
    layout: "mix",
    splitMenus: false,
    navTheme: "realDark", // "light",
    colorPrimary: "#1677FF",
    siderMenuType: "sub",
    fixedHeader: false,
  };

  if (typeof document === "undefined") {
    return <div />;
  }

  return (
    <Router>
      <ProLayout
        {...defaultProps}
        location={{
          pathname,
        }}
        token={{
          header: {
            colorBgMenuItemSelected: "rgba(0,0,0,0.04)",
          },
        }}
        menu={{
          collapsedShowGroupTitle: true,
        }}
        avatarProps={getAvatarProps()}
        actionsRender={(props) => getActionsRender(props)}
        headerTitleRender={headerTitleRender}
        menuFooterRender={menuFooterRender}
        onMenuHeaderClick={(e) => console.log(e)}
        menuItemRender={(item, dom) => menuItemRender(item, dom, setPathname)}
        {...settings}
      >
        <Routes>
          <Route
            path={"/dashboard"}
            element={
              <RouteCard>
                <DashboardPage />
              </RouteCard>
            }
          />
          <Route
            path={"/notes"}
            element={
              <RouteCard>
                <NotesPage />
              </RouteCard>
            }
          />
          <Route
            path={"/alerts"}
            element={
              <RouteCard>
                <Alerts />
              </RouteCard>
            }
          />
          <Route
            path={"/admin/sub-page1"}
            element={
              <RouteCard>
                <div>AQUI DOS</div>
              </RouteCard>
            }
          />
          <Route
            path={"/settings"}
            element={
              <RouteCard>
                <SettingsPage />
              </RouteCard>
            }
          />
        </Routes>
      </ProLayout>
    </Router>
  );
}

export default App;
