import React, { useState, useEffect } from "react";
import PptxGenJS from "pptxgenjs";
import html2canvas from "html2canvas";
import { DatePicker, Button, message } from "antd";
import dayjs from "dayjs";
import isoWeek from "dayjs/plugin/isoWeek";
dayjs.extend(isoWeek);
import api from "../../services/Agent";
import { useQuery } from "react-query";
import type { DashboardDataDto } from "@repo/shared";

import { DASHBOARD_THEME } from "./DashboardTheme";
import SectionBehavior from "./SectionBehavior";
import SectionSentiment from "./SectionSentiment";

const DashboardPage: React.FC = () => {
  const [selectedDates, setSelectedDates] = useState<
    [dayjs.Dayjs | null, dayjs.Dayjs | null] | null
  >(null);
  const [minDate, setMinDate] = useState<string | null>(null);
  const [maxDate, setMaxDate] = useState<string | null>(null);

  useEffect(() => {
    const fetchDates = async () => {
      try {
        const res = await api.post("/notes/dates", {});
        setMinDate(res.data.minDate);
        setMaxDate(res.data.maxDate);
        if (res.data.maxDate) {
          const max = dayjs(res.data.maxDate, "YYYY-MM-DD");
          setSelectedDates([max.startOf("isoWeek"), max.endOf("isoWeek")]);
        }
      } catch {
        setMinDate(null);
        setMaxDate(null);
      }
    };
    fetchDates();
  }, []);

  const {
    data: dashboardData,
    isLoading,
    error,
    refetch,
  } = useQuery<DashboardDataDto | null>(
    ["dashboard", selectedDates],
    async () => {
      if (!(selectedDates?.[0] && selectedDates?.[1])) return null;
      const res = await api.post("/notes/dashboard", {
        startDate: selectedDates[0]?.format("YYYY-MM-DD"),
        endDate: selectedDates[1]?.format("YYYY-MM-DD"),
      });
      return res.data as DashboardDataDto;
    },
    { enabled: !!selectedDates?.[0] && !!selectedDates?.[1] },
  );

  const handleDateChange = (
    dates: [dayjs.Dayjs | null, dayjs.Dayjs | null] | null,
  ) => {
    setSelectedDates(dates);
    refetch();
  };

  // ---------- Fecha formateada ----------

  const getCurrentWeek = (): [dayjs.Dayjs, dayjs.Dayjs] => {
    const today = dayjs();
    return [today.startOf("isoWeek"), today.endOf("isoWeek")];
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
  if (selectedDates?.[0] && selectedDates?.[1]) {
    const inicio = selectedDates[0];
    const fin = selectedDates[1];
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
    const hasMedia = (dashboardData?.sentiment.mediaData.length ?? 0) > 0;
    if (!hasSentiment && !hasMedia) {
      message.warning("No hay datos para exportar");
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
      message.success("Presentación descargada correctamente");
    } catch (err) {
      console.error(err);
      message.error("Error al generar la presentación");
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
      <div id="dashboard-fixed-sections">
        {dashboardData?.behavior && (
          <SectionBehavior
            dateRange={fechaRango}
            behaviorData={dashboardData.behavior}
          />
        )}
        <SectionSentiment
          dateRange={fechaRango}
          mediaData={dashboardData?.sentiment.mediaData ?? []}
        />
      </div>
    );
  }

  return (
    <div>
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          marginBottom: 24,
        }}
      >
        <div>
          <DatePicker.RangePicker
            value={selectedDates}
            onChange={handleDateChange}
            style={{ minWidth: 240 }}
            allowClear={false}
            disabledDate={(current) => {
              if (!minDate || !maxDate) return false;
              const min = dayjs(minDate, "YYYY-MM-DD");
              const max = dayjs(maxDate, "YYYY-MM-DD");
              return current < min || current > max;
            }}
            renderExtraFooter={() => (
              <button
                style={{ width: "100%" }}
                onClick={() => setSelectedDates(getCurrentWeek())}
              >
                Seleccionar semana actual
              </button>
            )}
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
      <div>{content}</div>
    </div>
  );
};

export default DashboardPage;
