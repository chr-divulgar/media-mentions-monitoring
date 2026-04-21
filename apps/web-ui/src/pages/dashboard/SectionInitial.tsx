import React from "react";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionInitialProps } from "./types";

const SectionInitial: React.FC<SectionInitialProps> = ({ dateRange }) => {
  return (
    <div
      style={{
        ...DASHBOARD_THEME.sectionContainer,
        backgroundImage: "url('/initial.png')",
        backgroundSize: "cover",
        backgroundPosition: "center",
        backgroundRepeat: "no-repeat",
        display: "flex",
      }}
    >
      <div style={{ flex: 4 }} />
      <div
        style={{
          flex: 2,
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          alignItems: "center",
          textAlign: "center",
          gap: 16,
        }}
      >
        <div
          style={{
            color: "#FFFFFF",
            fontWeight: "bold",
            textAlign: "center",
            fontSize: 40,
            lineHeight: 1.2,
          }}
        >
          Análisis Monitoreo Medios de Comunicación
        </div>
        <div
          style={{
            color: "#FFFFFF",
            fontWeight: "bold",
            textAlign: "center",
            fontSize: 20,
            marginTop: 24,
          }}
        >
          {dateRange}
        </div>
        <div
          style={{
            color: "#FFFFFF",
            fontWeight: "bold",
            textAlign: "center",
            fontSize: 20,
            lineHeight: 1.2,
          }}
        >
          Jefatura de Comunicaciones Externas y Prensa
        </div>
      </div>
    </div>
  );
};

export default SectionInitial;
