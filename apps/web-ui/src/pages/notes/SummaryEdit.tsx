import { useEffect, useMemo, useRef, useState } from "react";
import {
  Button,
  Col,
  Form,
  FormInstance,
  FormProps,
  Input,
  message,
  Row,
  Spin,
} from "antd";
import {
  GetSummaryDto,
  GetTranscriptionDto,
  NoteOrigin,
  NoteSentiment,
  SummaryDto,
  TranscriptionDto,
  transformText,
} from "@repo/shared/index";
import TextArea from "antd/es/input/TextArea";
import { useAlert } from "../alerts/AlertsContext";
import { AxiosError } from "axios";
import { useMutation, UseMutationResult } from "react-query";
import api from "../../services/Agent";
import Title from "antd/es/typography/Title";
import { useNote } from "./NoteContext";
import dayjs from "dayjs";
import AlertNoteContext from "./noteFields/AlertNoteContext";
import ClassificationFields from "./noteFields/ClassificationFields";
import ProgramAndRateFields from "./noteFields/ProgramAndRateFields";
import { useNoteFields } from "./noteFields/useNoteFields";
import WaveSurfer from "wavesurfer.js";
import { withAuthToken } from "../../services/authToken";
import {
  DownloadOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
} from "@ant-design/icons";

type TranscriptionMutationResult = UseMutationResult<
  TranscriptionDto,
  unknown,
  GetTranscriptionDto,
  unknown
>;
type SummaryMutationResult = UseMutationResult<
  SummaryDto,
  unknown,
  GetSummaryDto,
  unknown
>;

// Every field of the note's form; the ones the alert already knows are filled in and read-only.
type FieldType = {
  index?: string;
  program?: string;
  audioLabel?: string;
  title?: string;
  summary?: string;
};

interface SummaryEditProps {
  form: FormInstance<FieldType>;
  onFinish: () => void;
  // TV alerts work on the recorded video: the fragment is an MP4 and is played in a <video>.
  mediaKind?: "audio" | "video";
}
const SummaryEdit: React.FC<SummaryEditProps> = ({ form, onFinish, mediaKind = "audio" }) => {
  const { selectedAlert } = useAlert();
  const { note, setNote } = useNote();
  const getSummaryDtoRef = useRef(new GetSummaryDto()); // Replace initialGetSummaryDtoValue with the initial value
  const noteFields = useNoteFields(form, { lockContext: true, indexForAllMedia: true });

  // What the alert and its clip already say about the note. The date and time are the clip's own,
  // shown at Colombia time like the alerts list.
  useEffect(() => {
    if (!selectedAlert) return;
    const when = dayjs.utc(note?.startTime ?? selectedAlert.startTime).utcOffset(-300);
    const media = (selectedAlert.media ?? "").toLowerCase();
    form.setFieldsValue({
      clientName: selectedAlert.clientName,
      media: media === "television" ? "tv" : media,
      mediaName: selectedAlert.platform,
      date: when,
      startTime: when,
      duration: note?.duration,
    } as never);
  }, [selectedAlert?.id, note?.startTime, note?.duration, form]);

  const waveformUrl = useMemo(
    () =>
      withAuthToken(
        `${import.meta.env.VITE_API_LOCAL}/${mediaKind === "video" ? "video" : "audio"}/fetchByName/fragment_${selectedAlert?.id}?v=${Date.now()}`,
      ),
    [selectedAlert, mediaKind],
  );

  const getTranscriptionDto: GetTranscriptionDto = useMemo(
    () =>
      selectedAlert
        ? {
            filename: `fragment_${selectedAlert.id}.wav`,
          }
        : new GetTranscriptionDto(),
    [selectedAlert],
  );

  // WaveSurfer state/refs
  const waveRef = useRef<WaveSurfer | null>(null);
  const [isPlaying, setIsPlaying] = useState(false);

  useEffect(() => {
    // The video flow plays the clip in a <video> element; there is no waveform to build.
    if (!waveformUrl || mediaKind === "video") return;

    // Destroy previous instance
    waveRef.current?.destroy();

    const ws = WaveSurfer.create({
      container: "#waveform-container",
      waveColor: "#9aa0a6",
      progressColor: "#1890ff",
      cursorColor: "#333",
      height: 80,
      url: waveformUrl,
      interact: true,
    });

    ws.on("play", () => setIsPlaying(true));
    ws.on("pause", () => setIsPlaying(false));
    ws.on("finish", () => setIsPlaying(false));
    // When the user interacts with the waveform (e.g., seeks), start playing from that point
    ws.on("interaction", () => {
      ws.play();
    });

    waveRef.current = ws;

    return () => {
      ws.destroy();
      waveRef.current = null;
    };
  }, [waveformUrl, mediaKind]);

  const togglePlayPause = () => waveRef.current?.playPause();

  const handleDownloadMP3 = async () => {
    try {
      // Fetch the audio file
      const response = await fetch(waveformUrl);
      const blob = await response.blob();

      // Create a download link
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `fragment_${selectedAlert?.id}.${mediaKind === "video" ? "mp4" : "mp3"}`;
      document.body.appendChild(a);
      a.click();

      // Clean up
      window.URL.revokeObjectURL(url);
      document.body.removeChild(a);

      message.success(mediaKind === "video" ? "Video descargado exitosamente" : "Audio descargado exitosamente");
    } catch (error) {
      message.error(mediaKind === "video" ? "Error al descargar el video" : "Error al descargar el audio");
      console.error("Download error:", error);
    }
  };
  const {
    mutateAsync: generateTranscription,
    data: transcriptionData,
    isLoading: isLoadingTranscription,
    error: errorTranscription,
  }: TranscriptionMutationResult = useMutation<
    TranscriptionDto,
    unknown,
    GetTranscriptionDto,
    unknown
  >(
    async () => {
      const response = await api.post(`/alerts/getText`, getTranscriptionDto);
      return response.data;
    },
    {
      onSuccess: (transcriptionData) => {
        // Trigger the second mutation when the first one is successful
        if (!selectedAlert || !transcriptionData) return;
        getSummaryDtoRef.current = {
          noteId: transcriptionData?.noteId,
          text: transcriptionData?.text,
          words: selectedAlert.words,
        };
      },
    },
  );

  // Second mutation
  const {
    mutateAsync: generateSummary,
    data: summaryData,
    isLoading: isLoadingSummary,
    error: errorSummary,
  }: SummaryMutationResult = useMutation<
    SummaryDto,
    unknown,
    GetSummaryDto,
    unknown
  >(
    async () => {
      const response = await api.post(
        `/alerts/getSummary`,
        getSummaryDtoRef.current,
      );
      return response.data;
    },
    {
      onSuccess: (summaryData) => {
        if (!summaryData) return;
        setNote({
          ...note,
          id: getSummaryDtoRef.current.noteId,
          title: summaryData.title,
          summary: summaryData.summary,
        });
      },
    },
  );

  useEffect(() => {
    if (summaryData) {
      form.setFieldsValue({
        title: summaryData.title,
        summary: summaryData.summary,
      });
    }
  }, [summaryData, form]);

  const onFinishFailed: FormProps<FieldType>["onFinishFailed"] = (
    errorInfo,
  ) => {
    message.error(
      `Form submission failed. Please complete all required fields. ${errorInfo.errorFields.map((field) => field.name).join(", ")}`,
    );
  };

  if (!selectedAlert) return <div>No alert selected</div>;

  return waveformUrl ? (
    <Row gutter={[16, 0]}>
      <Col span={12}>
        <Row style={{ marginBottom: "12px" }}>
          <div
            style={{ whiteSpace: "normal" }}
            dangerouslySetInnerHTML={{
              __html: transformText(
                selectedAlert.text ?? "",
                selectedAlert.words ?? [],
              ),
            }}
          />
        </Row>
        <Row
          align="middle"
          justify="space-between"
          style={{ marginRight: "24px" }}
        >
          {mediaKind === "video" ? (
            <video
              src={waveformUrl}
              controls
              preload="metadata"
              style={{ width: "70%", maxHeight: "40vh", background: "#000" }}
            />
          ) : (
            <>
              <Button
                icon={isPlaying ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
                onClick={togglePlayPause}
                size="small"
              >
                {isPlaying ? "Pausar" : "Reproducir"}
              </Button>

              {/* WaveSurfer container replaces the native <audio> for better scrubbing */}
              <div id="waveform-container" style={{ width: "70%" }} />
            </>
          )}

          <Button
            icon={<DownloadOutlined />}
            onClick={handleDownloadMP3}
            size="small"
            title={mediaKind === "video" ? "Descargar MP4" : "Descargar MP3"}
          />

          <Button
            type="primary"
            onClick={() => generateTranscription(getTranscriptionDto)}
            size="small"
            disabled={!!isLoadingTranscription}
          >
            Texto
          </Button>
        </Row>

        {errorTranscription ? (
          <div>
            Error cargando la transcripción:{" "}
            {(errorTranscription as AxiosError<{ error?: string }>)?.response?.data?.error ?? "no se pudo transcribir"}
          </div>
        ) : (
          <div>
            <Spin spinning={isLoadingTranscription}>
              <Title level={4}>Transcripción</Title>
              <TextArea
                value={transcriptionData?.text}
                placeholder="Transcripción..."
                autoSize={{ minRows: 10, maxRows: 20 }}
                style={{ height: "100%" }}
              />
            </Spin>
            <Row style={{ margin: "12px" }} justify="end">
              <Button
                type="primary"
                onClick={() => generateSummary(getSummaryDtoRef.current)}
                size="small"
                disabled={!transcriptionData}
              >
                Resumen
              </Button>
            </Row>
          </div>
        )}
      </Col>
      <Col span={12}>
        {errorSummary ? (
          <div>Error cargando el resumen</div>
        ) : (
          <Form
            name="basic"
            layout="vertical"
            labelCol={{ span: 8 }}
            wrapperCol={{ span: 20 }}
            initialValues={{
              title: summaryData?.title,
              summary: summaryData?.summary,
              origin: NoteOrigin.DIRECTA,
              sentiment: NoteSentiment.NEUTRO,
            }}
            onFinish={onFinish}
            onFinishFailed={onFinishFailed}
            autoComplete="off"
            form={form}
          >
            <AlertNoteContext />
            <Form.Item name="duration" hidden>
              <Input />
            </Form.Item>
            <ProgramAndRateFields
              isRadioOrTelevision={noteFields.isRadioOrTelevision}
              programOptions={noteFields.programOptions}
            />
            <ClassificationFields
              topicOptions={noteFields.topicOptions}
              subtopicOptions={noteFields.subtopicOptions}
              subsubtopicOptions={noteFields.subsubtopicOptions}
              disableSubtopic={noteFields.disableSubtopic}
              disableSubsubtopic={noteFields.disableSubsubtopic}
            />
            <Spin spinning={isLoadingSummary || isLoadingTranscription}>
              <Form.Item<FieldType>
                label="Titular"
                name="title"
                rules={[{ required: true }]}
              >
                <Input placeholder="Titular..." />
              </Form.Item>
              <Form.Item<FieldType>
                label="Resumen"
                name="summary"
                rules={[{ required: true }]}
              >
                <TextArea
                  placeholder="Resumen..."
                  autoSize={{ minRows: 6, maxRows: 20 }}
                  style={{ height: "100%" }}
                />
              </Form.Item>
            </Spin>
          </Form>
        )}
      </Col>
    </Row>
  ) : null;
};

export default SummaryEdit;
