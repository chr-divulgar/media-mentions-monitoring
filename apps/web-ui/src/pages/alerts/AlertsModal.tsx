import React, { useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  Button,
  Col,
  Form,
  message,
  Modal,
  Radio,
  Row,
  Space,
  Spin,
  Steps,
  Typography,
} from "antd";
import { AxiosError } from "axios";
import { useAlert } from "./AlertsContext";
import { useMutation, UseMutationResult } from "react-query";
import {
  CreateFileDto,
  FileDto,
  Fragment,
  MAX_SEGMENT_WINDOW_SECONDS,
  NoteDto,
  SEGMENT_EXTENSION_STEP_SECONDS,
} from "@repo/shared/index";
import api from "../../services/Agent";
import AudioEdit from "../audio/AudioEdit";
import VideoEdit from "../audio/VideoEdit";
import { isFromToday, isTvAlert } from "./alertMedia";
import moment from "moment";
import SummaryEdit from "../notes/SummaryEdit";
import { useNote } from "../notes/NoteContext";
import NoteEdit from "../notes/NoteEdit";

// The backend's createFile endpoint already returns the real failure reason as
// `{ error: string }` in its response body — show that instead of a generic label.
const describeCreateFileError = (error: unknown, fallback: string): string =>
  (error as AxiosError<{ error?: string }>)?.response?.data?.error ?? fallback;

interface AlertsModalProps {
  visible: boolean;
  onClose: () => void;
}

// TV alerts work on the recorded video (the /video routes); everything else on the audio.
const useCreateFileMutation = (createFileDto: CreateFileDto, video: boolean) => {
  return useMutation<FileDto, unknown, CreateFileDto, unknown>(async () => {
    const response = await api.post(
      video ? `/video/createFile` : `/audio/createFile`,
      createFileDto,
    );
    return response.data;
  });
};
type CreateFileMutationResult = UseMutationResult<
  FileDto,
  unknown,
  CreateFileDto,
  unknown
>;

const AlertsModal: React.FC<AlertsModalProps> = ({ visible, onClose }) => {
  const { selectedAlert } = useAlert();
  const { note, setNote } = useNote();
  const [fragment, setFragment] = useState<Fragment>(new Fragment());
  const [createFragmentDto, setCreateFragmentDto] = useState<CreateFileDto>(
    new CreateFileDto()
  );
  const [formSummary] = Form.useForm();
  // A TV alert whose video is missing can fall back to the audio flow (today's alerts only: they
  // still have their audio on this machine; earlier days' audio is purged).
  const [audioFallback, setAudioFallback] = useState(false);
  const isVideo = isTvAlert(selectedAlert) && !audioFallback;
  // The window defaults to 30 min (1800s) for audio and video alike; the user can widen it to 1 or 2 hours,
  // which brings in the adjacent hours' recordings (audio.service.ts, and the worker joins the MP4s).
  const [segmentDuration, setSegmentDuration] = useState(1800);
  // Seconds added to either side of that window with the "+10 min" buttons (each click adds a step).
  const [extraBefore, setExtraBefore] = useState(0);
  const [extraAfter, setExtraAfter] = useState(0);
  // The extensions the last window that loaded had, to go back to them when the audio asked for is not there.
  const lastGoodExtras = useRef({ before: 0, after: 0 });

  const createSegmentDto = useMemo(() => {
    const extended = extraBefore > 0 || extraAfter > 0;
    const before = segmentDuration / 2 + extraBefore;
    const after = segmentDuration / 2 + extraAfter;
    return {
      alert: selectedAlert,
      // Without extensions the request is the usual one (same name, same centred window). With them it says how
      // much audio goes before and after the end of the alert, and the name keeps the files apart.
      output: extended
        ? `segment_${selectedAlert?.id}_b${before / 60}_a${after / 60}`
        : `segment_${selectedAlert?.id}_${segmentDuration / 60}`,
      duration: segmentDuration + extraBefore + extraAfter,
      ...(extended && { before, after }),
    };
  }, [selectedAlert, segmentDuration, extraBefore, extraAfter]);

  const {
    mutateAsync: createSegmentFile,
    data: segmentData,
    isLoading: isLoadingSegment,
    error: errorSegment,
  }: CreateFileMutationResult = useCreateFileMutation(createSegmentDto, isVideo);

  const {
    mutateAsync: createFragmentFile,
    data: fragmentData,
    isLoading: isLoadingFragment,
    error: errorFragment,
  }: CreateFileMutationResult = useCreateFileMutation(createFragmentDto, isVideo);

  const {
    mutateAsync: setNoteMutation,
    data: noteData,
    isLoading: isLoadingNote,
    error: errorNote,
  }: UseMutationResult<NoteDto, unknown, NoteDto, unknown> = useMutation<
    NoteDto,
    unknown,
    NoteDto,
    unknown
  >(async () => {
    const response = await api.post(`/notes/set-note`, note);
    return response.data;
  });

  useEffect(() => {
    if (segmentData) {
      lastGoodExtras.current = { before: extraBefore, after: extraAfter };
    }
    // Only when a window finishes loading: the extensions it had are the ones to go back to.
  }, [segmentData]);

  useEffect(() => {
    if (!errorSegment) return;
    const { before, after } = lastGoodExtras.current;
    if (extraBefore === before && extraAfter === after) return; // the failure is not from an extension
    message.error(
      `${describeCreateFileError(errorSegment, "No hay audio para esa ventana.")} Se volvió a la ventana anterior.`
    );
    setExtraBefore(before);
    setExtraAfter(after);
  }, [errorSegment]);

  useEffect(() => {
    if (visible) {
      setNote({});
      setFragment(new Fragment());
      setCreateFragmentDto(new CreateFileDto());
      createSegmentFile(createSegmentDto);
    }
  }, [visible, createSegmentFile, createSegmentDto, setNote]);

  const handleCreateFragment = (
    createFileDto: CreateFileDto,
    fragment: Fragment
  ) => {
    setFragment(fragment);
    setCreateFragmentDto(createFileDto);
  };

  useEffect(() => {
    if (!fragment) return;
    setNote({
      ...note,
      startTime: fragment.startTime?.toISOString(),
      // The whole length in seconds (seconds() is only the 0-59 part: a 3:20 clip was saved as 20 s).
      duration:
        fragment?.duration === undefined
          ? undefined
          : Math.round(fragment.duration.asSeconds()),
    });
  }, [fragment]);

  const [current, setCurrent] = useState(0);
  useEffect(() => {
    if (current === 1) {
      createFragmentFile(createFragmentDto);
    }
  }, [createFragmentDto, createFragmentFile, current]);

  const next = () => {
    if (current === 1) {
      if (note?.id) {
        formSummary.submit();
      } else {
        message.error("Debe generar el texto antes de continuar");
        return;
      }
    } else {
      setCurrent(current + 1);
    }
  };

  const onFinish = () => {
    formSummary.validateFields().then((values) => {
      // The form holds every field of the note (the alert's own data, the program slot, rate,
      // classification...). The date is the clip's Colombia day; the start time stays the clip's
      // exact instant and the duration its real length.
      const fields = { ...values };
      delete fields.startTime;
      delete fields.duration;
      delete fields.date;
      setNote({
        ...note,
        ...fields,
        date: values.date?.format("YYYY-MM-DD"),
        startTime: note?.startTime,
        duration: note?.duration,
      });
      setCurrent(current + 1);
    });
  };

  useEffect(() => {
    if (current === 2) {
      if (!note) return;
      setNoteMutation(note);
    }
  }, [current, note, setNoteMutation]);

  const prev = () => {
    setCurrent(current - 1);
  };

  // 404 from /video/createFile means there is no recording for that moment (or it was never
  // uploaded to the archive server), not a failure: say so, and offer the audio when it still exists.
  const videoNotAvailable =
    isVideo && (errorSegment as AxiosError | null)?.response?.status === 404;

  // The "+10 min" buttons stop at the longest window the server accepts, and the one after stops at the present:
  // there is no audio of what has not happened yet.
  const stepSeconds = SEGMENT_EXTENSION_STEP_SECONDS;
  const windowSeconds = segmentDuration + extraBefore + extraAfter;
  const alertEndMs = selectedAlert?.endTime ? new Date(selectedAlert.endTime).getTime() : NaN;
  const windowWouldEndInTheFuture =
    Number.isFinite(alertEndMs) &&
    alertEndMs + (segmentDuration / 2 + extraAfter + stepSeconds) * 1000 > Date.now();
  const canExtend = !isLoadingSegment && windowSeconds + stepSeconds <= MAX_SEGMENT_WINDOW_SECONDS;
  const resetExtras = () => {
    setExtraBefore(0);
    setExtraAfter(0);
  };

  const windowOptions = [
    { value: 1800, label: "30 min" },
    { value: 3600, label: "1 hora" },
    { value: 7200, label: "2 horas" },
  ];

  const steps = [
    {
      title: isVideo ? "Editar Video" : "Editar Audio",
      content: (
        <>
          <Space style={{ marginBottom: 12 }} wrap>
            <Button
              onClick={() => setExtraBefore((seconds) => seconds + stepSeconds)}
              disabled={!canExtend}
            >
              +10 min antes
            </Button>
            <Radio.Group
              value={segmentDuration}
              onChange={(e) => {
                setSegmentDuration(e.target.value);
                resetExtras();
              }}
              disabled={isLoadingSegment}
            >
              {windowOptions.map((option) => (
                <Radio.Button key={option.value} value={option.value}>
                  {option.label}
                </Radio.Button>
              ))}
            </Radio.Group>
            <Button
              onClick={() => setExtraAfter((seconds) => seconds + stepSeconds)}
              disabled={!canExtend || windowWouldEndInTheFuture}
            >
              +10 min después
            </Button>
            {(extraBefore > 0 || extraAfter > 0) && (
              <Typography.Text type="secondary">
                {`Ventana: ${(segmentDuration / 2 + extraBefore) / 60} min antes · ${
                  (segmentDuration / 2 + extraAfter) / 60
                } min después`}
              </Typography.Text>
            )}
          </Space>
          {videoNotAvailable ? (
            <Alert
              type="warning"
              showIcon
              message={describeCreateFileError(errorSegment, "No hay video para esta alerta.")}
              description={
                isFromToday(selectedAlert)
                  ? "Puedes editar esta alerta con su audio."
                  : "El video de días anteriores se toma del servidor de archivo; todavía no está disponible."
              }
              action={
                isFromToday(selectedAlert) && (
                  <Button
                    size="small"
                    onClick={() => {
                      setAudioFallback(true);
                      setSegmentDuration(1800);
                      resetExtras();
                    }}
                  >
                    Usar audio
                  </Button>
                )
              }
            />
          ) : errorSegment ? (
            <div>{describeCreateFileError(errorSegment, "Error loading Segment")}</div>
          ) : (
            segmentData &&
            (isVideo ? (
              <VideoEdit
                segmentData={segmentData}
                videoFile={createSegmentDto.output}
                onCreateFragmentDto={handleCreateFragment}
              ></VideoEdit>
            ) : (
              <AudioEdit
                segmentData={segmentData}
                audioFile={createSegmentDto.output}
                onCreateFragmentDto={handleCreateFragment}
              ></AudioEdit>
            ))
          )}
        </>
      ),
    },
    {
      title: "Resumen",
      content: errorFragment ? (
        <div>{describeCreateFileError(errorFragment, "Error loading Fragment")}</div>
      ) : (
        segmentData && (
          <Spin spinning={isLoadingFragment}>
            {fragmentData && (
              <SummaryEdit
                form={formSummary}
                onFinish={onFinish}
                mediaKind={isVideo ? "video" : "audio"}
              ></SummaryEdit>
            )}
          </Spin>
        )
      ),
    },
    {
      title: "Nota",
      content: errorNote ? (
        <div>Error loading Note</div>
      ) : (
        noteData && (
          <Spin spinning={isLoadingNote}>
            <NoteEdit note={noteData}></NoteEdit>
          </Spin>
        )
      ),
    },
  ];

  const items = steps.map((item) => ({ key: item.title, title: item.title }));

  if (!selectedAlert) return <div>No alert selected</div>;

  function isStepInvalid(): boolean {
    let value = true;
    switch (current) {
      case 0:
        value = !createFragmentDto.duration || createFragmentDto.duration === 0;
        break;
      case 1:
        value = !note?.id;
        break;
      case 2:
        value = !note?.id;
        break;
      default:
        break;
    }
    return value;
  }

  return (
    <Modal
      open={visible}
      title={`${selectedAlert.platform} - ${selectedAlert.clientName}`}
      loading={isLoadingSegment}
      onCancel={onClose}
      width="100vw"
      destroyOnClose={true}
      footer={null}
      maskClosable={false}
    >
      <>
        <Steps current={current} items={items} size="small" />
        <div style={{ minHeight: "300px", marginTop: "20px" }}>
          {steps[current].content}
        </div>
        <Row style={{ marginTop: 24 }}>
          <Col span={3}>
            <Button
              size="small"
              style={{ margin: "0 8px" }}
              onClick={() => prev()}
              disabled={current === 0}
            >
              {current > 0 ? "Previous" : "_________"}
            </Button>
            {current < steps.length - 1 && (
              <Button
                type="primary"
                onClick={() => next()}
                size="small"
                disabled={isStepInvalid()}
              >
                Next
              </Button>
            )}
            {current === steps.length - 1 && (
              <Button
                type="primary"
                onClick={() => message.success("Processing complete!")}
                size="small"
              >
                Done
              </Button>
            )}
          </Col>

          <Col span={3}>
            <div>
              {"Fecha: " +
                (fragment.startTime
                  ? moment
                      .utc(fragment.startTime)
                      .utcOffset(-300)
                      .format("YYYY-MM-DD")
                  : "")}
            </div>
          </Col>
          <Col span={3}>
            <div>
              {"Inicio: " +
                (fragment.startTime
                  ? moment
                      .utc(fragment.startTime)
                      .utcOffset(-300)
                      .format("HH:mm:ss")
                  : "")}
            </div>
          </Col>
          <Col span={3}>
            <div>{`Duración: ${
              fragment.startTime
                ? moment
                    .utc(fragment.duration?.asMilliseconds())
                    .format("HH:mm:ss")
                : ""
            }`}</div>
          </Col>
        </Row>
      </>
    </Modal>
  );
};

export default AlertsModal;
