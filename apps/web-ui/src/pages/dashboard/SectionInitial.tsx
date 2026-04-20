import React from "react";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionInitialProps } from "./types";

const SectionInitial: React.FC<SectionInitialProps> = ({ dateRange }) => {
  const titleText = "Análisis Monitoreo de medios de comunicación";

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
      aria-label={`Sección inicial ${dateRange}`}
    >
      <div style={{ flex: 1 }} />
      <div
        style={{
          flex: 1,
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
            ...DASHBOARD_THEME.titleStyle,
            textAlign: "center",
            fontSize: 40,
            lineHeight: 1.2,
          }}
        >
          {titleText}
        </div>
        <div
          style={{
            ...DASHBOARD_THEME.titleStyle,
            textAlign: "center",
            fontSize: 24,
            lineHeight: 1.2,
          }}
        >
          Jefatura de comunicaciones externas y prensa
        </div>
      </div>
    </div>
  );
};

export default SectionInitial;
