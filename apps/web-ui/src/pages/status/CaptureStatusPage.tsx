import { DatePicker, Modal, Radio, Tabs, Timeline, Tooltip, Typography } from "antd";
import dayjs, { Dayjs } from "dayjs";
import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient, UseQueryResult } from "react-query";
import { io } from "socket.io-client";
import {
  CaptureHourStatus,
  CaptureHourStatusDto,
  CaptureIncidentDto,
  CaptureIncidentKind,
  CaptureSegmentState,
  CaptureStatusResponseDto,
  dateFormat,
} from "@repo/shared/index";
import api from "../../services/Agent";
import { YouTubeSettings } from "../settings/YouTubeSettings";
import { WhatsAppStatus } from "../settings/WhatsAppStatus";

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
  "no-session": "Sin grabación (conexión caída o reconectando)",
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
  "no-session": "Sin grabación (conexión caída o reconectando)",
};

// The "google-news" row has no audio session — each half-hour segment is one search cycle, not a
// recording stretch, so it gets its own wording for the same captured/gap/no-session states.
const WEB_NEWS_SEGMENT_LABEL: Record<CaptureSegmentState, string> = {
  captured: "Ciclo completo",
  gap: "Ciclo con fallos",
  "no-session": "Ciclo no corrió o falló",
};

const segmentLabelFor = (media: string | undefined, state: CaptureSegmentState) =>
  media === "internet" ? WEB_NEWS_SEGMENT_LABEL[state] : SEGMENT_LABEL[state];

const INCIDENT_LABEL: Record<CaptureIncidentKind, string> = {
  failed: "Falló",
  "retry-failed": "Reintento fallido",
  recovered: "Recuperada",
  excluded: "Excluida",
  "startup-failed": "No arrancó",
  "worker-started": "Worker iniciado",
  "worker-stopped": "Worker detenido",
  "cycle-ok": "Ciclo completado",
  "cycle-failed": "Ciclo falló",
  "article-alerted": "Nota alertada",
  "article-rejected": "Nota descartada",
  "article-failed": "Nota con error",
  "send-failed": "Envío incompleto",
};

const INCIDENT_COLOR: Record<CaptureIncidentKind, string> = {
  failed: "red",
  "retry-failed": "orange",
  recovered: "green",
  excluded: "gray",
  "startup-failed": "red",
  "worker-started": "blue",
  "worker-stopped": "blue",
  "cycle-ok": "green",
  "cycle-failed": "red",
  "article-alerted": "green",
  "article-rejected": "gray",
  "article-failed": "red",
  "send-failed": "orange",
};

// The worker sends times in Bogotá time ("2026-10-01T10:02:00.0000000-05:00"), so the clock text
// is read straight from the string instead of being converted to the browser's time zone.
const clockOf = (isoText: string) => isoText.substring(11, 16);

const incidentTime = (incident: CaptureIncidentDto) =>
  incident.until ? `${clockOf(incident.at)}–${clockOf(incident.until)}` : clockOf(incident.at);

// Rejected/failed web-news incidents end with " — <url>" (see WebNewsCaptureUseCase) so an
// operator can open the actual note straight from the status popup.
const URL_PATTERN = /(https?:\/\/\S+)/;

const linkifyReason = (reason: string) =>
  reason.split(URL_PATTERN).map((part, index) =>
    index % 2 === 1 ? (
      <a key={index} href={part} target="_blank" rel="noreferrer">
        {part}
      </a>
    ) : (
      part
    ),
  );

const withIncidentHint = (text: string, hour: CaptureHourStatusDto) => {
  const count = hour.incidents?.length ?? 0;
  if (count === 0) return text;
  return (
    <>
      <div>{text}</div>
      <div style={{ marginTop: 4 }}>
        {count} {count === 1 ? "evento" : "eventos"} — clic para ver por qué
      </div>
    </>
  );
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
  const [detail, setDetail] = useState<{ sourceId: string; hour: number } | null>(null);
  // Looked up from the live data on every render so the open detail keeps updating with the socket.
  const detailSource = detail ? data?.sources.find((s) => s.sourceId === detail.sourceId) : undefined;
  const detailHour = detailSource?.hours.find((h) => h.hour === detail?.hour);

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
                {source.hours.map((h) => {
                  const openDetail = (e: React.MouseEvent) => {
                    e.stopPropagation();
                    setDetail({ sourceId: source.sourceId, hour: h.hour });
                  };
                  return h.segments && h.segments.length > 0 ? (
                    <div
                      key={h.hour}
                      onClick={openDetail}
                      style={{ flex: 1, margin: "0 1px", position: "relative", borderRadius: 2, overflow: "hidden" }}
                    >
                      {h.segments.map((segment) => (
                        <Tooltip
                          key={segment.startSeconds}
                          title={withIncidentHint(
                            `${formatOffset(h.hour, segment.startSeconds)}–${formatOffset(h.hour, segment.endSeconds)} — ${segmentLabelFor(source.media, segment.state)}`,
                            h,
                          )}
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
                      title={withIncidentHint(
                        `${h.hour.toString().padStart(2, "0")}:00 — ${STATUS_LABEL[h.status]}${
                          h.coveragePercent != null ? ` (${h.coveragePercent}%)` : ""
                        }`,
                        h,
                      )}
                    >
                      <div
                        onClick={openDetail}
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
                  );
                })}
              </div>
            </div>
            );
          })}
        </div>
      )}

      <Modal
        open={detail != null}
        onCancel={() => setDetail(null)}
        footer={null}
        width={680}
        title={detail ? `${detail.sourceId} — ${dateStr} ${detail.hour.toString().padStart(2, "0")}:00` : ""}
      >
        {detailHour && <HourDetail hour={detailHour} media={detailSource?.media} />}
      </Modal>
    </>
  );
};

const HourDetail = ({ hour, media }: { hour: CaptureHourStatusDto; media?: string }) => {
  const incidents = hour.incidents ?? [];
  const interrupted = (hour.segments ?? []).filter((s) => s.state !== "captured");

  return (
    <div style={{ maxHeight: 480, overflowY: "auto" }}>
      <Typography.Paragraph>
        {STATUS_LABEL[hour.status]}
        {hour.coveragePercent != null ? ` — cobertura ${hour.coveragePercent}%` : ""}
      </Typography.Paragraph>

      {interrupted.length > 0 && (
        <>
          <Typography.Text strong>Tramos sin grabar</Typography.Text>
          <ul style={{ marginTop: 4 }}>
            {interrupted.map((segment) => (
              <li key={segment.startSeconds}>
                {formatOffset(hour.hour, segment.startSeconds)}–{formatOffset(hour.hour, segment.endSeconds)}{" "}
                — {segmentLabelFor(media, segment.state)}
              </li>
            ))}
          </ul>
        </>
      )}

      <Typography.Text strong>Qué pasó</Typography.Text>
      {incidents.length === 0 ? (
        <Typography.Paragraph type="secondary" style={{ marginTop: 4 }}>
          Sin eventos registrados para esta hora. El worker pudo estar detenido, o la hora es anterior
          al registro de eventos.
        </Typography.Paragraph>
      ) : (
        <Timeline
          style={{ marginTop: 12 }}
          items={incidents.map((incident) => ({
            color: INCIDENT_COLOR[incident.kind],
            children: (
              <div>
                <strong>{incidentTime(incident)}</strong> · {INCIDENT_LABEL[incident.kind]}
                {incident.count > 1 ? ` ×${incident.count}` : ""}
                {incident.reason && <div style={{ color: "#8c8c8c" }}>{linkifyReason(incident.reason)}</div>}
              </div>
            ),
          }))}
        />
      )}
    </div>
  );
};

const CaptureStatusPage = () => {
  const tabItems = [
    { key: "recording", label: "Grabación", children: <RecordingStatusTab /> },
    { key: "youtube", label: "YouTube", children: <YouTubeSettings /> },
    { key: "whatsapp", label: "WhatsApp", children: <WhatsAppStatus /> },
  ];

  return <Tabs defaultActiveKey="recording" items={tabItems} />;
};

export default CaptureStatusPage;
