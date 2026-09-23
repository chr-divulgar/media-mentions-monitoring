import { DatePicker, Radio, Tabs, Tooltip } from "antd";
import dayjs, { Dayjs } from "dayjs";
import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient, UseQueryResult } from "react-query";
import { io } from "socket.io-client";
import {
  CaptureHourStatus,
  CaptureSegmentState,
  CaptureStatusResponseDto,
  dateFormat,
} from "@repo/shared/index";
import api from "../../services/Agent";
import { YouTubeSettings } from "../settings/YouTubeSettings";

const STATUS_COLOR: Record<CaptureHourStatus, string> = {
  ok: "#52c41a",
  "gap-filled": "#faad14",
  "no-session": "#8c8c8c",
  excluded: "#595959",
  "no-data": "#d9d9d9",
  live: "#1677ff",
};

const STATUS_LABEL: Record<CaptureHourStatus, string> = {
  ok: "Sin espacios",
  "gap-filled": "Con espacios",
  "no-session": "Worker no corría",
  excluded: "Excluida",
  "no-data": "Sin datos",
  live: "En curso",
};

const SEGMENT_COLOR: Record<CaptureSegmentState, string> = {
  captured: "#52c41a",
  gap: "#faad14",
  "no-session": "#8c8c8c",
};

const SEGMENT_LABEL: Record<CaptureSegmentState, string> = {
  captured: "Grabó bien",
  gap: "Quedó vacío",
  "no-session": "Worker no corría",
};

const formatOffset = (hour: number, elapsedSeconds: number) => {
  const totalMinutes = hour * 60 + Math.floor(elapsedSeconds / 60);
  const h = Math.floor(totalMinutes / 60) % 24;
  const m = totalMinutes % 60;
  return `${h.toString().padStart(2, "0")}:${m.toString().padStart(2, "0")}`;
};

const RecordingStatusTab = () => {
  const [date, setDate] = useState<Dayjs>(dayjs());
  const dateStr = date.format(dateFormat);
  const isToday = date.isSame(dayjs(), "day");
  const queryClient = useQueryClient();

  const {
    data,
    isLoading,
    error,
  }: UseQueryResult<CaptureStatusResponseDto> = useQuery({
    queryKey: ["captureStatus", dateStr],
    queryFn: async () =>
      await api.post(`/status/capture`, { date: dateStr }).then((res) => res.data),
    // A past date is static once fetched. Today's live updates come from the socket below instead
    // of client-side polling — the worker's underlying data (5-minute checkpoints) doesn't change
    // fast enough to need both.
    refetchInterval: false,
  });

  useEffect(() => {
    if (!isToday) return;

    const socket = io(`${import.meta.env.VITE_API_LOCAL}/status`);
    socket.on("captureStatus", (payload: CaptureStatusResponseDto) => {
      queryClient.setQueryData(["captureStatus", dateStr], payload);
    });

    return () => {
      socket.disconnect();
    };
  }, [isToday, dateStr, queryClient]);

  const hours = data?.sources[0]?.hours.map((h) => h.hour) ?? [];

  const mediaTypes = useMemo(
    () => Array.from(new Set(data?.sources.map((s) => s.media).filter((m): m is string => !!m) ?? [])),
    [data],
  );
  const [mediaFilter, setMediaFilter] = useState<string | "all">("all");
  const [selectedSourceId, setSelectedSourceId] = useState<string | null>(null);

  const visibleSources = data?.sources.filter(
    (s) => mediaFilter === "all" || s.media === mediaFilter,
  );

  return (
    <>
      <div style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
        <DatePicker
          value={date}
          onChange={(value) => value && setDate(value)}
          allowClear={false}
          disabledDate={(current) => current > dayjs()}
        />
        {mediaTypes.length > 1 && (
          <Radio.Group value={mediaFilter} onChange={(e) => setMediaFilter(e.target.value)}>
            <Radio.Button value="all">Todas</Radio.Button>
            {mediaTypes.map((media) => (
              <Radio.Button key={media} value={media}>
                {media}
              </Radio.Button>
            ))}
          </Radio.Group>
        )}
      </div>

      {isLoading && <div style={{ marginTop: 12 }}>Cargando...</div>}
      {error && <div style={{ marginTop: 12 }}>Error al cargar el estatus del worker</div>}

      {data && (
        <div style={{ marginTop: 16, overflowX: "auto" }}>
          <div style={{ display: "flex", minWidth: 900 }}>
            <div style={{ width: 220, flexShrink: 0 }} />
            <div style={{ display: "flex", flex: 1 }}>
              {hours.map((hour) => (
                <div
                  key={hour}
                  style={{ flex: 1, textAlign: "center", fontSize: 11, color: "#8c8c8c" }}
                >
                  {hour.toString().padStart(2, "0")}
                </div>
              ))}
            </div>
          </div>

          {visibleSources?.map((source) => {
            const isSelected = selectedSourceId === source.sourceId;
            const isDimmed = selectedSourceId != null && !isSelected;
            return (
            <div
              key={source.sourceId}
              onClick={() => setSelectedSourceId(isSelected ? null : source.sourceId)}
              style={{
                display: "flex",
                alignItems: "center",
                marginBottom: 4,
                cursor: "pointer",
                opacity: isDimmed ? 0.3 : 1,
                background: isSelected ? "rgba(22, 119, 255, 0.12)" : "transparent",
                borderRadius: 4,
                transition: "opacity 0.15s, background 0.15s",
              }}
            >
              <div
                style={{
                  width: 220,
                  flexShrink: 0,
                  fontSize: 12,
                  paddingRight: 8,
                  whiteSpace: "nowrap",
                  overflow: "hidden",
                  textOverflow: "ellipsis",
                  opacity: source.isExcluded ? 0.5 : 1,
                  fontWeight: isSelected ? 600 : 400,
                }}
                title={source.sourceId}
              >
                {source.sourceId}
              </div>
              <div style={{ display: "flex", flex: 1, height: 22 }}>
                {source.hours.map((h) =>
                  h.segments && h.segments.length > 0 ? (
                    <div
                      key={h.hour}
                      style={{ flex: 1, margin: "0 1px", position: "relative", borderRadius: 2, overflow: "hidden" }}
                    >
                      {h.segments.map((segment) => (
                        <Tooltip
                          key={segment.startSeconds}
                          title={`${formatOffset(h.hour, segment.startSeconds)}–${formatOffset(h.hour, segment.endSeconds)} — ${SEGMENT_LABEL[segment.state]}`}
                        >
                          <div
                            style={{
                              position: "absolute",
                              top: 0,
                              bottom: 0,
                              left: `${(segment.startSeconds / 3600) * 100}%`,
                              width: `${((segment.endSeconds - segment.startSeconds) / 3600) * 100}%`,
                              background: SEGMENT_COLOR[segment.state],
                            }}
                          />
                        </Tooltip>
                      ))}
                    </div>
                  ) : (
                    <Tooltip
                      key={h.hour}
                      title={`${h.hour.toString().padStart(2, "0")}:00 — ${STATUS_LABEL[h.status]}${
                        h.coveragePercent != null ? ` (${h.coveragePercent}%)` : ""
                      }`}
                    >
                      <div
                        style={{
                          flex: 1,
                          margin: "0 1px",
                          borderRadius: 2,
                          background: STATUS_COLOR[h.status],
                          opacity:
                            h.status === "gap-filled" && h.coveragePercent != null
                              ? Math.max(h.coveragePercent / 100, 0.25)
                              : 1,
                        }}
                      />
                    </Tooltip>
                  ),
                )}
              </div>
            </div>
            );
          })}
        </div>
      )}
    </>
  );
};

const CaptureStatusPage = () => {
  const tabItems = [
    { key: "recording", label: "Grabación", children: <RecordingStatusTab /> },
    { key: "youtube", label: "YouTube", children: <YouTubeSettings /> },
  ];

  return <Tabs defaultActiveKey="recording" items={tabItems} />;
};

export default CaptureStatusPage;
