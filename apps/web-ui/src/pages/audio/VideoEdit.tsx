import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Col, message, Row, Slider, Space, Typography } from "antd";
import {
  CreateFileDto,
  FileDto,
  Fragment,
  transformText,
} from "@repo/shared/index";
import BarComponent from "../../components/Bar";
import { useAlert } from "../alerts/AlertsContext";
import {
  alertMarkerPositions,
  alertWindowInSegment,
  selectionToFragment,
} from "../alerts/alertTimeline";
import { withAuthToken } from "../../services/authToken";

const { Text } = Typography;

// Shortest selection the editor allows: a clip needs at least a moment of video to be useful.
const MIN_SELECTION_SECONDS = 1;
// Used when the alert's own window does not fall inside the segment.
const DEFAULT_SELECTION_SECONDS = 30;

const formatTime = (seconds: number): string => {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${String(minutes).padStart(2, "0")}:${String(whole % 60).padStart(2, "0")}`;
};

interface VideoEditProps {
  videoFile: string;
  segmentData: FileDto;
  onCreateFragmentDto: (
    createFragmentDto: CreateFileDto,
    fragment: Fragment,
  ) => void;
}

// The TV counterpart of AudioEdit: plays the segment's video and lets the user choose the start and
// end of the clip. It hands the same `(createFileDto, fragment)` pair up to the modal, so the rest of
// the flow (fragment file, transcription, note) does not care which editor produced it.
const VideoEdit: React.FC<VideoEditProps> = ({
  videoFile,
  segmentData,
  onCreateFragmentDto,
}) => {
  const { selectedAlert } = useAlert();
  const videoRef = useRef<HTMLVideoElement>(null);
  const stopAt = useRef<number | null>(null);

  // The query string changes when the segment is created again, so the browser never replays a
  // cached clip from a previous window under the same file name.
  const url = useMemo(
    () =>
      withAuthToken(
        `${import.meta.env.VITE_API_LOCAL}/video/fetchByName/${videoFile}?v=${segmentData.startSeconds}_${segmentData.duration}`,
      ),
    [videoFile, segmentData],
  );

  const maxSeconds = Math.max(MIN_SELECTION_SECONDS, Math.floor(segmentData.duration));

  // Starts on the alert's own window when it lies inside the clip, otherwise on its first seconds.
  const [range, setRange] = useState<[number, number]>(() => {
    if (!selectedAlert) return [0, Math.min(DEFAULT_SELECTION_SECONDS, maxSeconds)];
    const { startSecond, endSecond } = alertWindowInSegment(selectedAlert, segmentData);
    const start = Math.max(0, startSecond);
    const end = Math.min(maxSeconds, endSecond);
    return end - start >= MIN_SELECTION_SECONDS
      ? [start, end]
      : [0, Math.min(DEFAULT_SELECTION_SECONDS, maxSeconds)];
  });

  useEffect(() => {
    if (!selectedAlert) return;
    const { fragment, createFileDto } = selectionToFragment(
      selectedAlert,
      segmentData,
      range[0],
      range[1],
    );
    onCreateFragmentDto(createFileDto as CreateFileDto, fragment);
  }, [range, selectedAlert, segmentData, onCreateFragmentDto]);

  if (!selectedAlert) return <div>No alert selected</div>;

  const { startPosition, endPosition } = alertMarkerPositions(selectedAlert, segmentData);

  const markStart = () => {
    const now = videoRef.current?.currentTime ?? 0;
    const end = range[1] - now >= MIN_SELECTION_SECONDS ? range[1] : Math.min(maxSeconds, now + DEFAULT_SELECTION_SECONDS);
    setRange([now, end]);
  };

  const markEnd = () => {
    const now = videoRef.current?.currentTime ?? 0;
    if (now - range[0] < MIN_SELECTION_SECONDS) {
      message.warning("El fin debe quedar después del inicio.");
      return;
    }
    setRange([range[0], now]);
  };

  const playSelection = () => {
    const video = videoRef.current;
    if (!video) return;
    video.currentTime = range[0];
    stopAt.current = range[1];
    void video.play();
  };

  const onTimeUpdate = () => {
    const video = videoRef.current;
    if (video && stopAt.current !== null && video.currentTime >= stopAt.current) {
      video.pause();
      stopAt.current = null;
    }
  };

  return (
    <Row align="middle">
      <Col span={24} style={{ marginBottom: "12px" }}>
        <div
          style={{ whiteSpace: "normal" }}
          dangerouslySetInnerHTML={{
            __html: transformText(selectedAlert.text ?? "", selectedAlert.words ?? []),
          }}
        />
      </Col>
      <Col span={24}>
        <video
          ref={videoRef}
          src={url}
          controls
          preload="auto"
          onTimeUpdate={onTimeUpdate}
          onPause={() => (stopAt.current = null)}
          style={{ width: "100%", maxHeight: "55vh", background: "#000" }}
        />
        <Slider
          range
          min={0}
          max={maxSeconds}
          step={0.5}
          value={range}
          onChange={(value) => {
            const [start, end] = value as [number, number];
            if (end - start >= MIN_SELECTION_SECONDS) setRange([start, end]);
          }}
          tooltip={{ formatter: (value) => formatTime(value ?? 0) }}
          style={{ marginBottom: 0 }}
        />
        <BarComponent startPosition={startPosition} endPosition={endPosition} />
        <Space style={{ marginTop: 12 }} wrap>
          <Button size="small" onClick={markStart}>
            Marcar inicio aquí
          </Button>
          <Button size="small" onClick={markEnd}>
            Marcar fin aquí
          </Button>
          <Button size="small" type="primary" onClick={playSelection}>
            Ver selección
          </Button>
          <Text>
            {`Inicio ${formatTime(range[0])} · Fin ${formatTime(range[1])} · Duración ${Math.round(range[1] - range[0])} s`}
          </Text>
        </Space>
        <div>
          <Text type="secondary" style={{ fontSize: 12 }}>
            El clip puede incluir unos segundos de video antes del inicio marcado.
          </Text>
        </div>
      </Col>
    </Row>
  );
};

export default VideoEdit;
