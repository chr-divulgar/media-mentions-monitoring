import React from "react";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionFinalProps } from "./types";

const SectionFinal: React.FC<SectionFinalProps> = () => {
  return (
    <div
      style={{
        ...DASHBOARD_THEME.sectionContainer,
        backgroundImage: "url('/final.png')",
        backgroundSize: "cover",
        backgroundPosition: "center",
        backgroundRepeat: "no-repeat",
      }}
    />
  );
};

export default SectionFinal;
