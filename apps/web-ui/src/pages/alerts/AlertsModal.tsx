import React, { useEffect, useMemo, useState } from "react";
import {
  Button,
  Col,
  Form,
  message,
  Modal,
  Radio,
  Row,
  Spin,
  Steps,
} from "antd";
import { useAlert } from "./AlertsContext";
import { useMutation, UseMutationResult } from "react-query";
import { CreateFileDto, FileDto, Fragment, NoteDto } from "@repo/shared/index";
import api from "../../services/Agent";
import AudioEdit from "../audio/AudioEdit";
import moment from "moment";
import SummaryEdit from "../notes/SummaryEdit";
import { useNote } from "../notes/NoteContext";
import NoteEdit from "../notes/NoteEdit";

interface AlertsModalProps {
  visible: boolean;
  onClose: () => void;
}

const useCreateFileMutation = (createFileDto: CreateFileDto) => {
  return useMutation<FileDto, unknown, CreateFileDto, unknown>(async () => {
    const response = await api.post(`/audio/createFile`, createFileDto);
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
  // Default stays 30 min (1800s), matching today's behavior; the user can widen it
  // to bring in audio from the adjacent hour's recording (see audio.service.ts).
  const [segmentDuration, setSegmentDuration] = useState(1800);

  const createSegmentDto = useMemo(
    () => ({
      alert: selectedAlert,
      output: `segment_${selectedAlert?.id}_${segmentDuration / 60}`,
      duration: segmentDuration,
    }),
    [selectedAlert, segmentDuration]
  );

  const {
    mutateAsync: createSegmentFile,
    data: segmentData,
    isLoading: isLoadingSegment,
    error: errorSegment,
  }: CreateFileMutationResult = useCreateFileMutation(createSegmentDto);

  const {
    mutateAsync: createFragmentFile,
    data: fragmentData,
    isLoading: isLoadingFragment,
    error: errorFragment,
  }: CreateFileMutationResult = useCreateFileMutation(createFragmentDto);

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
      duration: fragment?.duration?.seconds(),
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
      setNote({
        ...note,
        index: values.index,
        program: values.program,
        title: values.title,
        summary: values.summary,
        audioLabel: values.audioLabel,
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

  const steps = [
    {
      title: "Editar Audio",
      content: (
        <>
          <Radio.Group
            value={segmentDuration}
            onChange={(e) => setSegmentDuration(e.target.value)}
            disabled={isLoadingSegment}
            style={{ marginBottom: 12 }}
          >
            <Radio.Button value={1800}>30 min</Radio.Button>
            <Radio.Button value={3600}>1 hora</Radio.Button>
            <Radio.Button value={7200}>2 horas</Radio.Button>
          </Radio.Group>
          {errorSegment ? (
            <div>Error loading Segment</div>
          ) : (
            segmentData && (
              <AudioEdit
                segmentData={segmentData}
                audioFile={createSegmentDto.output}
                onCreateFragmentDto={handleCreateFragment}
              ></AudioEdit>
            )
          )}
        </>
      ),
    },
    {
      title: "Resumen",
      content: errorFragment ? (
        <div>Error loading Fragment</div>
      ) : (
        segmentData && (
          <Spin spinning={isLoadingFragment}>
            {fragmentData && (
              <SummaryEdit form={formSummary} onFinish={onFinish}></SummaryEdit>
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
                  ? moment(fragment.startTime).format("YYYY-MM-DD")
                  : "")}
            </div>
          </Col>
          <Col span={3}>
            <div>
              {"Inicio: " +
                (fragment.startTime
                  ? moment(fragment.startTime).format("HH:mm:ss")
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
