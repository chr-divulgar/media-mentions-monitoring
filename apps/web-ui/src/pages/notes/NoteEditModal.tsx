import React from "react";
import {
  Modal,
  Form,
  Input,
  DatePicker,
  Select,
  Radio,
  Upload,
  Button,
  message,
  Collapse,
} from "antd";
import {
  CalendarOutlined,
  TeamOutlined,
  NotificationOutlined,
  TagsOutlined,
  PartitionOutlined,
  ApartmentOutlined,
  SwapOutlined,
  LikeOutlined,
  FileTextOutlined,
  LinkOutlined,
  EnvironmentOutlined,
  DollarOutlined,
  BarChartOutlined,
  PlayCircleOutlined,
  UploadOutlined,
  FolderOpenOutlined,
  InboxOutlined,
} from "@ant-design/icons";
import dayjs from "dayjs";
import { useQuery } from "react-query";
import api from "../../services/Agent";
import { NoteDto } from "@repo/shared/index";
import type { MediaTypeDto, PlatformDto } from "@repo/shared/index";
import styles from "./NoteEditModal.module.css";

interface ClientTopicNode {
  name?: string;
  subtopics?: Array<{
    name?: string;
    subsubtopics?: Array<{ name?: string }>;
  }>;
}

interface ClientWithTopics {
  id?: string;
  name?: string;
  topics?: ClientTopicNode[];
}

interface NoteEditModalProps {
  open: boolean;
  note: NoteDto | null;
  onSave: (note: NoteDto) => void;
  onCancel: () => void;
}

const REQUIRED_FIELD_TOOLTIP =
  "Este campo es obligatorio para guardar correctamente la nota.";

const NoteEditModal: React.FC<NoteEditModalProps> = ({
  open,
  note,
  onSave,
  onCancel,
}) => {
  const [form] = Form.useForm();
  const selectedMedia = Form.useWatch("media", form) as string | undefined;
  const selectedAttachment = Form.useWatch("attachment", form) as
    | string
    | undefined;
  const selectedMediaName = Form.useWatch("mediaName", form) as
    | string
    | undefined;
  const selectedClientName = Form.useWatch("clientName", form) as
    | string
    | undefined;
  const selectedTopicName = Form.useWatch("topic", form) as string | undefined;
  const selectedSubtopicName = Form.useWatch("subtopic", form) as
    | string
    | undefined;

  const { data: mediaTypes = [] } = useQuery<MediaTypeDto[]>(
    ["media-types"],
    async () => {
      const res = await api.get("/clients/media/all");
      return res.data as MediaTypeDto[];
    },
    { staleTime: Infinity },
  );

  const { data: platforms = [] } = useQuery<PlatformDto[]>(
    ["platforms-by-media", selectedMedia],
    async () => {
      const res = await api.get(`/settings/get-platforms/${selectedMedia}`);
      return res.data as PlatformDto[];
    },
    { enabled: !!selectedMedia, staleTime: 5 * 60 * 1000 },
  );

  const { data: clients = [] } = useQuery<ClientWithTopics[]>(
    ["clients"],
    async () => {
      const res = await api.get("/clients");
      return res.data as ClientWithTopics[];
    },
    { staleTime: 5 * 60 * 1000 },
  );

  const selectedClient = React.useMemo(
    () => clients.find((client) => client.name === selectedClientName),
    [clients, selectedClientName],
  );

  const topicOptions = React.useMemo(
    () =>
      (selectedClient?.topics ?? [])
        .filter((topic) => topic?.name)
        .map((topic) => ({ value: topic.name, label: topic.name })),
    [selectedClient],
  );

  const selectedTopic = React.useMemo(
    () =>
      (selectedClient?.topics ?? []).find(
        (topic) => topic.name === selectedTopicName,
      ),
    [selectedClient, selectedTopicName],
  );

  const subtopicOptions = React.useMemo(
    () =>
      (selectedTopic?.subtopics ?? [])
        .filter((subtopic) => subtopic?.name)
        .map((subtopic) => ({ value: subtopic.name, label: subtopic.name })),
    [selectedTopic],
  );

  const selectedSubtopic = React.useMemo(
    () =>
      (selectedTopic?.subtopics ?? []).find(
        (subtopic) => subtopic.name === selectedSubtopicName,
      ),
    [selectedTopic, selectedSubtopicName],
  );

  const subsubtopicOptions = React.useMemo(
    () =>
      (selectedSubtopic?.subsubtopics ?? [])
        .filter((subsubtopic) => subsubtopic?.name)
        .map((subsubtopic) => ({
          value: subsubtopic.name,
          label: subsubtopic.name,
        })),
    [selectedSubtopic],
  );

  const disableSubtopic = !selectedTopicName || subtopicOptions.length === 0;
  const disableSubsubtopic =
    !selectedSubtopicName || subsubtopicOptions.length === 0;

  const normalizeMedia = React.useCallback((value?: string) => {
    return (value ?? "")
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "");
  }, []);

  const getExpectedExtension = React.useCallback(
    (media?: string) => {
      const normalizedMedia = normalizeMedia(media);
      if (normalizedMedia.includes("radio")) return "mp3";
      if (normalizedMedia.includes("television")) return "mp4";
      return "pdf";
    },
    [normalizeMedia],
  );

  const getAcceptByMedia = React.useCallback(
    (media?: string) => {
      const expectedExtension = getExpectedExtension(media);
      if (expectedExtension === "mp3") return ".mp3,audio/mpeg";
      if (expectedExtension === "mp4") return ".mp4,video/mp4";
      return ".pdf,application/pdf";
    },
    [getExpectedExtension],
  );

  const isAttachmentAllowed = React.useCallback(
    (fileName: string, media?: string) => {
      const expectedExtension = getExpectedExtension(media);
      return fileName.toLowerCase().endsWith(`.${expectedExtension}`);
    },
    [getExpectedExtension],
  );

  const renderFieldLabel = React.useCallback(
    (icon: React.ReactNode, text: string) => (
      <span className={styles.fieldLabel}>
        <span className={styles.fieldLabelIcon}>{icon}</span>
        <span>{text}</span>
      </span>
    ),
    [],
  );

  const renderSectionLabel = React.useCallback(
    (icon: React.ReactNode, title: string, description: string) => (
      <div className={styles.sectionTitleWrap}>
        <span className={styles.sectionIcon}>{icon}</span>
        <span>
          <span className={styles.sectionTitle}>{title}</span>
          <span className={styles.sectionDescription}>{description}</span>
        </span>
      </div>
    ),
    [],
  );

  React.useEffect(() => {
    if (note) {
      form.setFieldsValue({
        ...note,
        date: note.date ? dayjs(note.date) : dayjs(),
        origin: note.origin ?? "Directa",
        sentiment: note.sentiment ?? "Neutra",
      });
    } else {
      form.setFieldsValue({
        date: dayjs(),
        origin: "Directa",
        sentiment: "Neutra",
      });
    }
  }, [note, form]);

  React.useEffect(() => {
    if (!selectedMedia) {
      form.setFieldValue("mediaName", undefined);
      return;
    }

    const currentMediaName = form.getFieldValue("mediaName");
    if (!currentMediaName) {
      return;
    }

    const existsInCurrentMedia = platforms.some(
      (platform) => platform.name === currentMediaName,
    );

    if (!existsInCurrentMedia) {
      form.setFieldValue("mediaName", undefined);
    }
  }, [selectedMedia, platforms, form]);

  React.useEffect(() => {
    if (!selectedAttachment) {
      return;
    }

    if (!isAttachmentAllowed(selectedAttachment, selectedMedia)) {
      form.setFieldValue("attachment", undefined);
    }
  }, [selectedAttachment, selectedMedia, isAttachmentAllowed, form]);

  React.useEffect(() => {
    if (!selectedMediaName) {
      form.setFieldsValue({ zone: undefined, city: undefined });
      return;
    }

    const selectedPlatform = platforms.find(
      (platform) => platform.name === selectedMediaName,
    );

    form.setFieldsValue({
      zone: selectedPlatform?.zone ?? undefined,
      city: selectedPlatform?.city ?? undefined,
    });
  }, [selectedMediaName, platforms, form]);

  React.useEffect(() => {
    if (!clients.length) {
      return;
    }

    const currentClientName = form.getFieldValue("clientName") as
      | string
      | undefined;
    if (!currentClientName) {
      form.setFieldsValue({ clientName: clients[0]?.name });
      return;
    }

    const exists = clients.some((client) => client.name === currentClientName);
    if (!exists) {
      form.setFieldsValue({
        clientName: clients[0]?.name,
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
    }
  }, [clients, form]);

  React.useEffect(() => {
    if (!selectedClientName) {
      form.setFieldsValue({
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
      return;
    }

    const currentTopic = form.getFieldValue("topic") as string | undefined;
    const validTopic = (selectedClient?.topics ?? []).some(
      (topic) => topic.name === currentTopic,
    );
    if (!validTopic) {
      form.setFieldsValue({
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
    }
  }, [selectedClientName, selectedClient, form]);

  React.useEffect(() => {
    const currentSubtopic = form.getFieldValue("subtopic") as
      | string
      | undefined;
    const validSubtopic = (selectedTopic?.subtopics ?? []).some(
      (subtopic) => subtopic.name === currentSubtopic,
    );
    if (!validSubtopic) {
      form.setFieldsValue({ subtopic: undefined, subsubtopic: undefined });
    }
  }, [selectedTopic, form]);

  React.useEffect(() => {
    const currentSubsubtopic = form.getFieldValue("subsubtopic") as
      | string
      | undefined;
    const validSubsubtopic = (selectedSubtopic?.subsubtopics ?? []).some(
      (subsubtopic) => subsubtopic.name === currentSubsubtopic,
    );
    if (!validSubsubtopic) {
      form.setFieldValue("subsubtopic", undefined);
    }
  }, [selectedSubtopic, form]);

  const handleOk = () => {
    form.validateFields().then((values) => {
      const formatted = {
        ...values,
        date: values.date ? values.date.format("YYYY-MM-DD") : undefined,
      };
      onSave({ ...note, ...formatted });
    });
  };

  return (
    <Modal
      open={open}
      title={
        <div className={styles.modalTitleBlock}>
          <span className={styles.modalEyebrow}>Registro</span>
          <span className={styles.modalTitle}>Información de nota</span>
          <span className={styles.modalSubtitle}>
            Captura los datos con claridad y una estructura adaptable.
          </span>
        </div>
      }
      onCancel={onCancel}
      width="min(1100px, calc(100vw - 24px))"
      wrapClassName={styles.noteModalWrap}
      footer={
        <div className={styles.modalFooterBar}>
          <Button
            size="large"
            onClick={onCancel}
            className={styles.cancelButton}
          >
            Cancelar
          </Button>
          <Button
            size="large"
            type="primary"
            onClick={handleOk}
            className={styles.saveButton}
          >
            Guardar
          </Button>
        </div>
      }
    >
      <Form
        form={form}
        layout="vertical"
        validateTrigger={["onChange", "onBlur"]}
        className={styles.darkForm}
      >
        <div className={styles.sectionsLayout}>
          <div className={styles.sectionStack}>
            <Collapse
              className={styles.sectionCollapse}
              defaultActiveKey={["general"]}
              items={[
                {
                  key: "general",
                  label: renderSectionLabel(
                    <NotificationOutlined />,
                    "Datos generales",
                    "Información base del medio y del cliente.",
                  ),
                  children: (
                    <div className={styles.sectionGrid}>
                      <Form.Item
                        name="date"
                        label={renderFieldLabel(<CalendarOutlined />, "Fecha")}
                        tooltip={REQUIRED_FIELD_TOOLTIP}
                        rules={[
                          {
                            required: true,
                            message: "El campo Fecha es obligatorio",
                          },
                        ]}
                      >
                        <DatePicker
                          style={{ width: "100%" }}
                          format="DD/MM/YYYY"
                          placeholder="Seleccionar fecha"
                          allowClear={false}
                        />
                      </Form.Item>
                      <Form.Item
                        name="clientName"
                        label={renderFieldLabel(<TeamOutlined />, "Cliente")}
                      >
                        <Select
                          showSearch
                          placeholder="Seleccionar cliente"
                          optionFilterProp="label"
                          options={clients
                            .filter((client) => client.name)
                            .map((client) => ({
                              value: client.name,
                              label: client.name,
                            }))}
                        />
                      </Form.Item>
                      <Form.Item
                        name="media"
                        label={renderFieldLabel(
                          <NotificationOutlined />,
                          "Medio",
                        )}
                        tooltip={REQUIRED_FIELD_TOOLTIP}
                        rules={[
                          {
                            required: true,
                            message: "El campo Medio es obligatorio",
                          },
                        ]}
                      >
                        <Select
                          showSearch
                          placeholder="Seleccionar medio"
                          optionFilterProp="label"
                          onChange={() =>
                            form.setFieldsValue({
                              mediaName: undefined,
                              attachment: undefined,
                            })
                          }
                          options={mediaTypes.map((mt) => ({
                            value: mt.name,
                            label: mt.label,
                          }))}
                        />
                      </Form.Item>
                      <Form.Item
                        name="mediaName"
                        label={renderFieldLabel(
                          <FolderOpenOutlined />,
                          "Nombre del medio",
                        )}
                        tooltip={REQUIRED_FIELD_TOOLTIP}
                        rules={[
                          {
                            required: true,
                            message: "El campo Nombre del Medio es obligatorio",
                          },
                        ]}
                      >
                        <Select
                          showSearch
                          disabled={!selectedMedia}
                          placeholder="Seleccione el nombre del medio"
                          optionFilterProp="label"
                          options={platforms
                            .filter((platform) => platform.name)
                            .map((platform) => ({
                              value: platform.name,
                              label: platform.name,
                            }))}
                        />
                      </Form.Item>
                      <Form.Item
                        name="zone"
                        label={renderFieldLabel(
                          <EnvironmentOutlined />,
                          "Zona",
                        )}
                      >
                        <Input disabled placeholder="Zona" />
                      </Form.Item>
                      <Form.Item
                        name="city"
                        label={renderFieldLabel(
                          <EnvironmentOutlined />,
                          "Ciudad",
                        )}
                      >
                        <Input disabled placeholder="Ciudad" />
                      </Form.Item>
                    </div>
                  ),
                },
              ]}
            />

            <Collapse
              className={styles.sectionCollapse}
              defaultActiveKey={["classification"]}
              items={[
                {
                  key: "classification",
                  label: renderSectionLabel(
                    <TagsOutlined />,
                    "Clasificación",
                    "Define la categoría editorial y el tono de la nota.",
                  ),
                  children: (
                    <div className={styles.sectionGrid}>
                      <Form.Item
                        name="topic"
                        label={renderFieldLabel(<TagsOutlined />, "Tema")}
                      >
                        <Select
                          showSearch
                          disabled={!selectedClientName}
                          placeholder="Seleccionar tema"
                          optionFilterProp="label"
                          options={topicOptions}
                        />
                      </Form.Item>
                      <Form.Item
                        name="subtopic"
                        label={renderFieldLabel(
                          <PartitionOutlined />,
                          "Subtema",
                        )}
                      >
                        <Select
                          showSearch
                          disabled={disableSubtopic}
                          placeholder="Seleccionar subtema"
                          optionFilterProp="label"
                          options={subtopicOptions}
                        />
                      </Form.Item>
                      <Form.Item
                        name="subsubtopic"
                        label={renderFieldLabel(
                          <ApartmentOutlined />,
                          "Subsubtema",
                        )}
                      >
                        <Select
                          showSearch
                          disabled={disableSubsubtopic}
                          placeholder="Seleccionar subsubtema"
                          optionFilterProp="label"
                          options={subsubtopicOptions}
                        />
                      </Form.Item>
                      <Form.Item
                        name="origin"
                        label={renderFieldLabel(<SwapOutlined />, "Origen")}
                      >
                        <Radio.Group
                          className={styles.radioGroup}
                          optionType="button"
                          buttonStyle="solid"
                        >
                          <Radio value="Directa">Directa</Radio>
                          <Radio value="Indirecta">Indirecta</Radio>
                        </Radio.Group>
                      </Form.Item>
                      <Form.Item
                        name="sentiment"
                        label={renderFieldLabel(
                          <LikeOutlined />,
                          "Sentimiento",
                        )}
                        className={styles.fullSpan}
                      >
                        <Radio.Group
                          className={styles.radioGroup}
                          optionType="button"
                          buttonStyle="solid"
                        >
                          <Radio value="Neutra">Neutra</Radio>
                          <Radio value="Positiva">Positiva</Radio>
                          <Radio value="Negativa">Negativa</Radio>
                        </Radio.Group>
                      </Form.Item>
                    </div>
                  ),
                },
              ]}
            />
          </div>

          <div className={styles.sectionStack}>
            <Collapse
              className={styles.sectionCollapse}
              defaultActiveKey={["content"]}
              items={[
                {
                  key: "content",
                  label: renderSectionLabel(
                    <FileTextOutlined />,
                    "Contenido",
                    "Texto principal, referencia y origen de publicación.",
                  ),
                  children: (
                    <div className={styles.sectionGrid}>
                      <Form.Item name="attachment" hidden>
                        <Input />
                      </Form.Item>
                      <Form.Item
                        name="title"
                        label={renderFieldLabel(<FileTextOutlined />, "Título")}
                        tooltip={REQUIRED_FIELD_TOOLTIP}
                        rules={[
                          {
                            required: true,
                            message: "El campo Título es obligatorio",
                          },
                        ]}
                        className={styles.fullSpan}
                      >
                        <Input placeholder="Ingresa un título claro" />
                      </Form.Item>

                      <Form.Item
                        name="summary"
                        label={renderFieldLabel(
                          <FileTextOutlined />,
                          "Resumen",
                        )}
                        className={styles.fullSpan}
                      >
                        <Input.TextArea
                          rows={4}
                          placeholder="Describe los puntos clave de la nota"
                        />
                      </Form.Item>
                      <Form.Item
                        label={renderFieldLabel(<UploadOutlined />, "Archivo")}
                        extra={`Formato permitido para ${selectedMedia ?? "otros medios"}: .${getExpectedExtension(selectedMedia)}`}
                        className={styles.fullSpan}
                      >
                        <Upload.Dragger
                          className={styles.attachmentDragger}
                          maxCount={1}
                          accept={getAcceptByMedia(selectedMedia)}
                          showUploadList={false}
                          beforeUpload={(file) => {
                            if (
                              !isAttachmentAllowed(file.name, selectedMedia)
                            ) {
                              message.error(
                                `Para ${selectedMedia ?? "otros medios"} solo se permite .${getExpectedExtension(selectedMedia)}`,
                              );
                              return Upload.LIST_IGNORE;
                            }

                            form.setFieldValue("attachment", file.name);
                            return false;
                          }}
                          onRemove={() => {
                            form.setFieldValue("attachment", undefined);
                          }}
                        >
                          <div className={styles.uploadInlineContent}>
                            <span className={styles.uploadInlineIcon}>
                              <InboxOutlined />
                            </span>
                            <span className={styles.uploadInlineTextGroup}>
                              <span className={styles.uploadInlineText}>
                                Arrastra el archivo aquí o haz click para cargar
                              </span>
                              <span className={styles.uploadInlineHint}>
                                {selectedAttachment
                                  ? `Archivo cargado: ${selectedAttachment}`
                                  : "No hay archivo seleccionado"}
                              </span>
                            </span>
                          </div>
                        </Upload.Dragger>
                      </Form.Item>
                      <Form.Item
                        name="link"
                        label={renderFieldLabel(<LinkOutlined />, "Link")}
                        className={styles.fullSpan}
                      >
                        <Input placeholder="https://..." />
                      </Form.Item>
                    </div>
                  ),
                },
              ]}
            />

            <Collapse
              className={styles.sectionCollapse}
              defaultActiveKey={["metrics"]}
              items={[
                {
                  key: "metrics",
                  label: renderSectionLabel(
                    <EnvironmentOutlined />,
                    "Métricas",
                    "Variables de valoración.",
                  ),
                  children: (
                    <div className={styles.sectionGrid}>
                      <Form.Item
                        name="value"
                        label={renderFieldLabel(<DollarOutlined />, "Valor")}
                      >
                        <Input placeholder="Valor estimado" />
                      </Form.Item>
                      <Form.Item
                        name="rate"
                        label={renderFieldLabel(<DollarOutlined />, "Tarifa")}
                      >
                        <Input placeholder="Tarifa" />
                      </Form.Item>
                      <Form.Item
                        name="audience"
                        label={renderFieldLabel(
                          <BarChartOutlined />,
                          "Audiencia",
                        )}
                      >
                        <Input placeholder="Audiencia alcanzada" />
                      </Form.Item>
                      <Form.Item
                        name="program"
                        label={renderFieldLabel(
                          <PlayCircleOutlined />,
                          "Programa",
                        )}
                      >
                        <Input placeholder="Nombre del programa" />
                      </Form.Item>
                    </div>
                  ),
                },
              ]}
            />
          </div>
        </div>
      </Form>
    </Modal>
  );
};

export default NoteEditModal;
