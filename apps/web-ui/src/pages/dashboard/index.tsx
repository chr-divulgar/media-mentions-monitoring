import React, { useState, useEffect } from "react";
import PptxGenJS from "pptxgenjs";
import html2canvas from "html2canvas";
import { DatePicker, Button, message, Select } from "antd";
import dayjs from "dayjs";
import isoWeek from "dayjs/plugin/isoWeek";
dayjs.extend(isoWeek);
import api from "../../services/Agent";
import { useQuery } from "react-query";
import type { DashboardDataDto, DashboardPeriod } from "@repo/shared";

import { DASHBOARD_THEME } from "./DashboardTheme";
import SectionBehavior from "./SectionBehavior";
import SectionSentiment from "./SectionSentiment";
import SectionByZone from "./SectionByZone";
import SectionPerformance from "./SectionPerformance";
import SectionTop20ByMediaName from "./SectionTop20ByMediaName";
import SectionByMedia from "./SectionByMedia";
import SectionPresident from "./SectionPresident";
import SectionInitial from "./SectionInitial";
import SectionFinal from "./SectionFinal";

const DashboardPage: React.FC = () => {
  const [messageApi, contextHolder] = message.useMessage();
  const [selectedPeriod, setSelectedPeriod] =
    useState<DashboardPeriod>("semana");
  const [selectedDates, setSelectedDates] = useState<
    [dayjs.Dayjs | null, dayjs.Dayjs | null] | null
  >(null);
  const [minDate, setMinDate] = useState<string | null>(null);
  const [maxDate, setMaxDate] = useState<string | null>(null);

  const getRangeForPeriod = (
    period: DashboardPeriod,
    maxDateValue: string,
  ): [dayjs.Dayjs, dayjs.Dayjs] => {
    const max = dayjs(maxDateValue, "YYYY-MM-DD");

    if (period === "semana") {
      return [max.startOf("isoWeek"), max.endOf("isoWeek")];
    }

    if (period === "mes") {
      return [max.startOf("month"), max.endOf("month")];
    }

    if (period === "trimestre") {
      const quarterStartMonth = Math.floor(max.month() / 3) * 3;
      const quarterStart = max.month(quarterStartMonth).startOf("month");
      const quarterEnd = quarterStart.add(2, "month").endOf("month");
      return [quarterStart, quarterEnd];
    }

    return [max.startOf("year"), max];
  };

  useEffect(() => {
    const fetchDates = async () => {
      try {
        const res = await api.post("/notes/dates", {});
        setMinDate(res.data.minDate);
        setMaxDate(res.data.maxDate);
      } catch {
        setMinDate(null);
        setMaxDate(null);
      }
    };
    fetchDates();
  }, []);

  useEffect(() => {
    if (!maxDate) return;
    setSelectedDates(getRangeForPeriod(selectedPeriod, maxDate));
  }, [maxDate, selectedPeriod]);

  const {
    data: dashboardData,
    isLoading,
    error,
  } = useQuery<DashboardDataDto | null>(
    ["dashboard", selectedDates, selectedPeriod],
    async () => {
      if (!(selectedDates?.[0] && selectedDates?.[1])) return null;

      const maxDataDate = maxDate ? dayjs(maxDate, "YYYY-MM-DD") : null;
      const startRequestDate =
        selectedPeriod === "semana"
          ? selectedDates[0]
          : selectedDates[0].startOf("month");
      const rawEndRequestDate =
        selectedPeriod === "semana"
          ? selectedDates[1]
          : selectedDates[1].endOf("month");
      const endRequestDate =
        maxDataDate && rawEndRequestDate.isAfter(maxDataDate)
          ? maxDataDate
          : rawEndRequestDate;

      const res = await api.post("/notes/dashboard", {
        startDate: startRequestDate.format("YYYY-MM-DD"),
        endDate: endRequestDate.format("YYYY-MM-DD"),
        period: selectedPeriod,
      });
      return res.data as DashboardDataDto;
    },
    { enabled: !!selectedDates?.[0] && !!selectedDates?.[1] },
  );

  const handleDateChange = (
    dates: [dayjs.Dayjs | null, dayjs.Dayjs | null] | null,
  ) => {
    setSelectedDates(dates);
  };

  const handlePeriodChange = (period: DashboardPeriod) => {
    setSelectedPeriod(period);
    if (!maxDate) return;
    setSelectedDates(getRangeForPeriod(period, maxDate));
  };

  // ---------- Fecha formateada ----------

  const getCurrentWeek = (): [dayjs.Dayjs, dayjs.Dayjs] => {
    const today = dayjs();
    return [today.startOf("isoWeek"), today.endOf("isoWeek")];
  };

  const getNormalizedDateRange = (): [dayjs.Dayjs, dayjs.Dayjs] | null => {
    if (!selectedDates?.[0] || !selectedDates?.[1]) return null;

    const maxDataDate = maxDate ? dayjs(maxDate, "YYYY-MM-DD") : null;
    const startDate =
      selectedPeriod === "semana"
        ? selectedDates[0]
        : selectedDates[0].startOf("month");
    const rawEndDate =
      selectedPeriod === "semana"
        ? selectedDates[1]
        : selectedDates[1].endOf("month");
    const endDate =
      maxDataDate && rawEndDate.isAfter(maxDataDate) ? maxDataDate : rawEndDate;

    return [startDate, endDate];
  };

  const meses = [
    "enero",
    "febrero",
    "marzo",
    "abril",
    "mayo",
    "junio",
    "julio",
    "agosto",
    "septiembre",
    "octubre",
    "noviembre",
    "diciembre",
  ];

  let fechaRango = "";
  const normalizedRange = getNormalizedDateRange();
  if (normalizedRange) {
    const [inicio, fin] = normalizedRange;
    const diaInicio = inicio.format("DD");
    const mesInicio = meses[inicio.month()];
    const diaFin = fin.format("DD");
    const mesFin = meses[fin.month()];
    const anio = fin.format("YYYY");
    if (mesInicio === mesFin) {
      fechaRango = `${mesInicio.charAt(0).toUpperCase() + mesInicio.slice(1)} ${diaInicio} a ${diaFin} de ${anio}`;
    } else {
      fechaRango = `${mesInicio.charAt(0).toUpperCase() + mesInicio.slice(1)} ${diaInicio} a ${mesFin.charAt(0).toUpperCase() + mesFin.slice(1)} ${diaFin} de ${anio}`;
    }
  }

  // ---------- Exportar a PowerPoint ----------

  const handleExportPPTX = async () => {
    const hasSentiment =
      (dashboardData?.behavior.sentimentData.length ?? 0) > 0;
    if (!hasSentiment) {
      messageApi.warning("No hay datos para exportar");
      return;
    }
    try {
      const pptx = new PptxGenJS();
      const fixedSections = document.querySelectorAll(
        "#dashboard-fixed-sections > div",
      );
      for (const sectionEl of fixedSections) {
        const sectionDiv = sectionEl as HTMLDivElement;
        const slide = pptx.addSlide();
        slide.background = { color: DASHBOARD_THEME.slideBgHex };
        await new Promise((res) => setTimeout(res, 300));
        const canvas = await html2canvas(sectionDiv, {
          backgroundColor: DASHBOARD_THEME.sectionBg,
          useCORS: true,
          width: 960,
          height: 540,
          scale: 1,
        });
        const imgData = canvas.toDataURL("image/png");
        slide.addImage({ data: imgData, x: 0, y: 0, w: 10, h: 5.625 });
      }
      await pptx.writeFile({ fileName: "dashboard-notas.pptx" });
      messageApi.success("Presentación descargada correctamente");
    } catch (err) {
      console.error(err);
      messageApi.error("Error al generar la presentación");
    }
  };

  // ---------- Contenido ----------

  let content;
  if (isLoading) {
    content = <div>Cargando datos...</div>;
  } else if (error) {
    content = <div>Error al cargar datos</div>;
  } else {
    content = (
      <div id="dashboard-fixed-sections" style={{ fontSize: 16 }}>
        <SectionInitial dateRange={fechaRango} period={selectedPeriod} />
        {dashboardData?.behavior && (
          <SectionBehavior
            dateRange={fechaRango}
            period={selectedPeriod}
            behaviorData={dashboardData.behavior}
          />
        )}
        {dashboardData?.sentiment && (
          <SectionSentiment
            dateRange={fechaRango}
            period={selectedPeriod}
            sentimentData={dashboardData.sentiment}
          />
        )}
        {dashboardData?.performance && (
          <SectionPerformance
            dateRange={fechaRango}
            period={selectedPeriod}
            performanceData={dashboardData.performance}
          />
        )}
        {dashboardData?.tableByMediaName && (
          <SectionTop20ByMediaName
            dateRange={fechaRango}
            period={selectedPeriod}
            tableDataByMediaName={dashboardData.tableByMediaName}
          />
        )}

        {dashboardData?.tableByMedia &&
          dashboardData.tableByMedia.length > 0 && (
            <SectionByMedia
              dateRange={fechaRango}
              period={selectedPeriod}
              tableByMedia={dashboardData.tableByMedia}
            />
          )}
        {dashboardData?.president && (
          <SectionPresident
            dateRange={fechaRango}
            period={selectedPeriod}
            presidentData={dashboardData.president}
          />
        )}
        {dashboardData?.sectionByZone && (
          <SectionByZone
            dateRange={fechaRango}
            period={selectedPeriod}
            sectionByZone={dashboardData.sectionByZone}
          />
        )}
        <SectionFinal dateRange={fechaRango} period={selectedPeriod} />
      </div>
    );
  }

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        height: "calc(100vh - 220px)",
        minHeight: 0,
        overflow: "hidden",
      }}
    >
      {contextHolder}
      <div
        style={{
          position: "sticky",
          top: 0,
          zIndex: 10,
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          padding: "12px 0",
          borderBottom: "1px solid #f0f0f0",
          marginBottom: 16,
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <Select<DashboardPeriod>
            value={selectedPeriod}
            onChange={handlePeriodChange}
            style={{ minWidth: 140 }}
            options={[
              { value: "semana", label: "Semana" },
              { value: "mes", label: "Mes" },
              { value: "trimestre", label: "Trimestre" },
              { value: "anual", label: "Anual" },
            ]}
          />
          <DatePicker.RangePicker
            value={selectedDates}
            onChange={handleDateChange}
            picker={selectedPeriod === "semana" ? "date" : "month"}
            format={selectedPeriod === "semana" ? "YYYY-MM-DD" : "YYYY-MM"}
            style={{ minWidth: 240 }}
            allowClear={false}
            disabledDate={(current) => {
              if (!minDate || !maxDate) return false;
              const min = dayjs(minDate, "YYYY-MM-DD");
              const max = dayjs(maxDate, "YYYY-MM-DD");

              if (selectedPeriod !== "semana") {
                return (
                  current.endOf("month").isBefore(min, "day") ||
                  current.startOf("month").isAfter(max, "day")
                );
              }

              return current < min || current > max;
            }}
            renderExtraFooter={
              selectedPeriod === "semana"
                ? () => (
                    <button
                      style={{ width: "100%" }}
                      onClick={() => setSelectedDates(getCurrentWeek())}
                    >
                      Seleccionar semana actual
                    </button>
                  )
                : undefined
            }
          />
        </div>
        <div>
          <Button
            type="primary"
            onClick={handleExportPPTX}
            style={{ marginLeft: 16 }}
          >
            Descargar PPTX
          </Button>
        </div>
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>{content}</div>
    </div>
  );
};

export default DashboardPage;
