import { DatePicker, Radio } from "antd";
import dayjs from "dayjs";
import { useState } from "react";
import { dateFormat, DateRange, ValidDatesDto } from "@repo/shared/index";
import AlertsTable from "./AlertsTable";
import { useQuery, UseQueryResult } from "react-query";
import api from "../../services/Agent";

// Asegúrate de tener un formato de fecha válido para dayjs

const { RangePicker } = DatePicker;

type AlertsView = "legacy" | "worker" | "compare";

const Alerts = () => {
  const {
    data: dates,
    isLoading: isLoadingDates,
    error: errorDates,
  }: UseQueryResult<ValidDatesDto> = useQuery({
    queryKey: ["dates"],
    queryFn: async () =>
      await api.post(`/alerts/dates`, {}).then((res) => res.data),
  });

  const [selectedDates, setSelectedDates] = useState<DateRange | null>([
    dayjs(),
    dayjs(),
  ]);
  const [view, setView] = useState<AlertsView>("legacy");

  const handleCalendarChange = (dates: DateRange | null) => {
    setSelectedDates(dates);
  };

  if (isLoadingDates) return <div>Loading...</div>;
  if (errorDates) return <div>Error loading Dates</div>;

  const minDate = dayjs(dates?.minDate, dateFormat);
  const maxDate = dayjs(dates?.maxDate, dateFormat);

  return (
    <>
      <RangePicker
        value={selectedDates || [maxDate, maxDate]}
        onChange={handleCalendarChange}
        disabledDate={(current) => current < minDate || current > maxDate}
        allowClear={false}
      />
      <div style={{ margin: "12px 0" }}>
        <Radio.Group value={view} onChange={(e) => setView(e.target.value)}>
          <Radio.Button value="legacy">Legacy</Radio.Button>
          <Radio.Button value="worker">Worker</Radio.Button>
          <Radio.Button value="compare">Comparar</Radio.Button>
        </Radio.Group>
      </div>
      {view === "compare" ? (
        <div
          style={{
            display: "flex",
            flexWrap: "wrap",
            gap: 16,
          }}
        >
          <div style={{ flex: "1 1 400px", minWidth: 0 }}>
            <AlertsTable
              selectedDates={selectedDates}
              source="legacy"
              title="Versión actual (legacy)"
            />
          </div>
          <div style={{ flex: "1 1 400px", minWidth: 0 }}>
            <AlertsTable
              selectedDates={selectedDates}
              source="worker"
              title="Versión nueva (worker)"
            />
          </div>
        </div>
      ) : (
        <AlertsTable selectedDates={selectedDates} source={view} />
      )}
    </>
  );
};

export default Alerts;
