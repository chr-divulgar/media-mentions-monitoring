import React from "react";
import {
  Modal,
  Form,
  Input,
  DatePicker,
  TimePicker,
  Select,
  Radio,
  Upload,
  Button,
  Space,
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
import { NoteDto } from "@repo/shared/index";
import { useNoteFields } from "./noteFields/useNoteFields";
import { normalizeMedia } from "./noteFields/noteMedia";
import styles from "./NoteEditModal.module.css";

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
  const {
    mediaTypes,
    platforms,
    clients,
    selectedMedia,
    selectedAttachment,
    selectedClientName,
    hasGeneratedPdfLink,
    isRadioOrTelevision,
    topicOptions,
    subtopicOptions,
    subsubtopicOptions,
    disableSubtopic,
    disableSubsubtopic,
    programOptions,
    canSelectTime,
    getFileDurationInSeconds,
    getExpectedExtension,
    getAcceptByMedia,
    isAttachmentAllowed,
  } = useNoteFields(form, { noteId: note?.id });


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
      let startTimeValue: dayjs.Dayjs | undefined;
      if (note.startTime) {
        const normalizedStartTime = /^\d{2}:\d{2}$/.test(note.startTime)
          ? `2000-01-01T${note.startTime}:00`
          : note.startTime;
        startTimeValue = dayjs(normalizedStartTime);
      }

      form.setFieldsValue({
        ...note,
        date: note.date ? dayjs(note.date) : dayjs(),
        startTime: startTimeValue,
        origin: note.origin ?? "Directa",
        sentiment: note.sentiment ?? "Neutra",
      });
    } else {
      form.setFieldsValue({
        date: dayjs(),
        media: "radio",
        origin: "Directa",
        sentiment: "Neutra",
      });
    }
  }, [note, form]);


  const handleOk = () => {
    form.validateFields().then((values) => {
      const normalizedMedia = normalizeMedia(values.media);
      const currentIsRadioOrTelevision =
        normalizedMedia.includes("radio") ||
        normalizedMedia.includes("television") ||
        normalizedMedia.includes("tv");

      const formatted = {
        ...values,
        date: values.date ? values.date.format("YYYY-MM-DD") : undefined,
        startTime: values.startTime
          ? values.startTime.format("HH:mm")
          : undefined,
        audioLabel: currentIsRadioOrTelevision ? values.audioLabel : undefined,
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
                              startTime: undefined,
                              program: undefined,
                              audioLabel: undefined,
                              rate: undefined,
                              audience: undefined,
                              value: undefined,
                              duration: undefined,
                              index: undefined,
                            })
                          }
                          options={mediaTypes.map((mt) => ({
                            value: mt.name,
                            label: mt.label,
                          }))}
                        />
                      </Form.Item>
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
                        name="startTime"
                        label={renderFieldLabel(
                          <CalendarOutlined />,
                          "Hora y minutos",
                        )}
                        tooltip={
                          canSelectTime
                            ? undefined
                            : "Selecciona primero medio y fecha"
                        }
                        rules={[
                          {
                            required: true,
                            message: "Selecciona la hora de publicación",
                          },
                        ]}
                      >
                        <TimePicker
                          style={{ width: "100%" }}
                          format="HH:mm"
                          minuteStep={1}
                          placeholder="Seleccionar hora"
                          allowClear
                          disabled={!canSelectTime || !isRadioOrTelevision}
                          showNow={false}
                          needConfirm={false}
                          onChange={(time) => {
                            form.setFieldsValue({ startTime: time });
                          }}
                          onCalendarChange={(time) => {
                            form.setFieldsValue({ startTime: time });
                          }}
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
                      <Form.Item
                        name="value"
                        label={renderFieldLabel(<DollarOutlined />, "Valor")}
                      >
                        <Input
                          placeholder="Valor estimado"
                          disabled={isRadioOrTelevision}
                        />
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
                      {hasGeneratedPdfLink ? (
                        <Form.Item
                          label={renderFieldLabel(<UploadOutlined />, "PDF archivado")}
                          extra="Generado automáticamente al crear la nota; no se puede modificar."
                          className={styles.fullSpan}
                        >
                          <Space.Compact style={{ width: "100%" }}>
                            <Input value={selectedAttachment} disabled />
                            <Button href={selectedAttachment} target="_blank">
                              Abrir PDF
                            </Button>
                          </Space.Compact>
                        </Form.Item>
                      ) : (
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
                            beforeUpload={async (file) => {
                              if (
                                !isAttachmentAllowed(file.name, selectedMedia)
                              ) {
                                message.error(
                                  `Para ${selectedMedia ?? "otros medios"} solo se permite .${getExpectedExtension(selectedMedia)}`,
                                );
                                return Upload.LIST_IGNORE;
                              }

                              if (isRadioOrTelevision) {
                                const seconds = await getFileDurationInSeconds(
                                  file,
                                  selectedMedia,
                                );
                                form.setFieldValue("duration", seconds);
                                if (!seconds) {
                                  message.warning(
                                    "No se pudo calcular la duración del archivo.",
                                  );
                                }
                              }

                              form.setFieldValue("attachment", file.name);
                              return false;
                            }}
                            onRemove={() => {
                              form.setFieldsValue({
                                attachment: undefined,
                                duration: undefined,
                              });
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
                      )}
                      {isRadioOrTelevision && (
                        <>
                          <Form.Item
                            name="duration"
                            label={renderFieldLabel(
                              <PlayCircleOutlined />,
                              "Segundos",
                            )}
                          >
                            <Input
                              disabled
                              placeholder="Duración en segundos"
                            />
                          </Form.Item>
                          <Form.Item
                            name="index"
                            label={renderFieldLabel(
                              <FileTextOutlined />,
                              "NOTA ##",
                            )}
                            rules={[
                              {
                                required: true,
                                message: "No se pudo calcular el consecutivo",
                              },
                            ]}
                          >
                            <Input disabled placeholder="01" />
                          </Form.Item>
                        </>
                      )}
                      <Form.Item
                        name="program"
                        label={renderFieldLabel(
                          <PlayCircleOutlined />,
                          "Programa",
                        )}
                        className={styles.fullSpan}
                        rules={
                          isRadioOrTelevision
                            ? [
                                {
                                  required: true,
                                  message: "Selecciona una franja",
                                },
                              ]
                            : undefined
                        }
                      >
                        {isRadioOrTelevision ? (
                          <Select
                            placeholder="Selecciona franja"
                            options={programOptions.map((option) => ({
                              value: option.value,
                              label: option.label,
                            }))}
                            disabled={!programOptions.length}
                          />
                        ) : (
                          <Input placeholder="Nombre del programa" />
                        )}
                      </Form.Item>
                      {isRadioOrTelevision && (
                        <Form.Item
                          name="audioLabel"
                          label={renderFieldLabel(
                            <FileTextOutlined />,
                            "Audio label",
                          )}
                          className={styles.fullSpan}
                        >
                          <Input
                            disabled
                            placeholder="NOTA_##_AUDIO_LABEL.ext"
                          />
                        </Form.Item>
                      )}
                      <Form.Item
                        name="rate"
                        label={renderFieldLabel(<DollarOutlined />, "Tarifa")}
                      >
                        <Input
                          placeholder="Tarifa"
                          disabled={isRadioOrTelevision}
                        />
                      </Form.Item>
                      <Form.Item
                        name="audience"
                        label={renderFieldLabel(
                          <BarChartOutlined />,
                          "Audiencia",
                        )}
                      >
                        <Input
                          placeholder="Audiencia alcanzada"
                          disabled={isRadioOrTelevision}
                        />
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
          </div>
        </div>
      </Form>
    </Modal>
  );
};

export default NoteEditModal;
