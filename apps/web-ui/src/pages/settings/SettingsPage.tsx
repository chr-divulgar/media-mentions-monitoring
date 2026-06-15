import React, { useState } from "react";
import {
  Button,
  Table,
  Modal,
  Form,
  Input,
  Select,
  Space,
  message,
  Typography,
  Card,
  Tabs,
  Tag,
  Popconfirm,
  Alert,
  Row,
  Col,
  Divider,
  TimePicker,
} from "antd";
import dayjs from "dayjs";
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  UserOutlined,
  MinusCircleOutlined,
} from "@ant-design/icons";
import { useQuery, useMutation, useQueryClient } from "react-query";
import api from "../../services/Agent";
import type {
  PlatformDto,
  ClientDto,
  MediaTypeDto,
  WordDto,
  SlotDto,
} from "@repo/shared";

const { Title, Text } = Typography;
const { Option } = Select;

const ROLE_OPTIONS = ["admin", "user", "initial"];

// ─── Types ────────────────────────────────────────────────────────────────────

interface FirebaseUserDto {
  uid?: string;
  email: string;
  displayName?: string;
  password?: string;
  role?: string;
  phone?: string;
  disabled?: boolean;
}

// ─── Slot generation helpers ────────────────────────────────────────────────

function nameToDisplay(name: string): string {
  // "CaracolBucaramanga" → "Caracol Bucaramanga"
  return name.replace(/([A-Z])/g, " $1").trim();
}

function nameToAudio(name: string): string {
  return nameToDisplay(name).toUpperCase().replace(/\s+/g, "_");
}

function generateDefaultSlots(name: string): SlotDto[] {
  const display = nameToDisplay(name);
  const audio = nameToAudio(name);
  const def = { audience: 5000, rate: 105000 };
  return [
    {
      day: "weekday" as SlotDto["day"],
      start: "00:00",
      end: "04:00",
      label: `${display}: Noticias de la Madrugada:`,
      audioLabel: `_${audio}_AM_`,
      ...def,
    },
    {
      day: "weekday" as SlotDto["day"],
      start: "04:00",
      end: "12:00",
      label: `${display}: Noticias de la Ma\u00f1ana:`,
      audioLabel: `_${audio}_AM_`,
      ...def,
    },
    {
      day: "weekday" as SlotDto["day"],
      start: "12:00",
      end: "13:00",
      label: `${display}: Noticias del Medio D\u00eda:`,
      audioLabel: `_${audio}_MD_`,
      ...def,
    },
    {
      day: "weekday" as SlotDto["day"],
      start: "13:00",
      end: "19:00",
      label: `${display}: Noticias de la Tarde:`,
      audioLabel: `_${audio}_TARDE_`,
      ...def,
    },
    {
      day: "weekday" as SlotDto["day"],
      start: "19:00",
      end: "23:59",
      label: `${display}: Noticias de la Noche:`,
      audioLabel: `_${audio}_NOCHE_`,
      ...def,
    },
    {
      day: "saturday" as SlotDto["day"],
      start: "00:00",
      end: "23:59",
      label: `${display}: Fin de semana:`,
      audioLabel: `_${audio}_FIN_DE_SEMANA_`,
      ...def,
    },
    {
      day: "sunday" as SlotDto["day"],
      start: "00:00",
      end: "23:59",
      label: `${display}: Fin de semana:`,
      audioLabel: `_${audio}_FIN_DE_SEMANA_`,
      ...def,
    },
  ];
}

const ZONE_OPTIONS = [
  "Internacional",
  "Nacional",
  "Otros Departamentos",
  "Arauca Casanare",
  "Meta",
  "Magdalena Medio",
  "Región Caribe",
  "Región Sur",
];

// Cities are fetched dynamically from api-colombia.com
const STATIC_CITY_OPTIONS = [
  { value: "Internacional", label: "Internacional" },
  { value: "Nacional", label: "Nacional" },
];

// ─── Platform form ────────────────────────────────────────────────────────────

const DAY_OPTIONS = [
  { value: "weekday", label: "Días de semana" },
  { value: "saturday", label: "Sábado" },
  { value: "sunday", label: "Domingo" },
];

const DAY_LABELS: Record<string, string> = {
  weekday: "Días de semana",
  saturday: "Sábado",
  sunday: "Domingo",
};

function validateDayCoverage(slots: SlotDto[], day: string): string | null {
  const daySlots = slots
    .filter((s) => s.day === day)
    .sort((a, b) => a.start.localeCompare(b.start));
  if (!daySlots.length) return `Faltan franjas para ${DAY_LABELS[day]}`;
  if (daySlots[0].start !== "00:00")
    return `${DAY_LABELS[day]}: la primera franja debe iniciar en 00:00`;
  for (let i = 0; i < daySlots.length - 1; i++) {
    if (daySlots[i].end !== daySlots[i + 1].start)
      return `${DAY_LABELS[day]}: hay un hueco entre ${daySlots[i].end} y ${daySlots[i + 1].start}`;
  }
  if (daySlots[daySlots.length - 1].end !== "23:59")
    return `${DAY_LABELS[day]}: la última franja debe terminar en 23:59`;
  return null;
}

const SlotsField: React.FC<{ platformName?: string }> = ({
  platformName = "",
}) => {
  const form = Form.useFormInstance();
  return (
    <Form.List name="slots">
      {(fields, { add, remove }) => (
        <>
          {DAY_OPTIONS.map(({ value: day, label: dayLabel }) => (
            <Form.Item key={day} noStyle shouldUpdate>
              {({ getFieldValue }) => {
                const dayFields = fields.filter(
                  ({ name }) => getFieldValue(["slots", name, "day"]) === day,
                );

                const getSlotValues = (name: number) => ({
                  start: getFieldValue(["slots", name, "start"]) ?? "",
                  end: getFieldValue(["slots", name, "end"]) ?? "",
                  label: getFieldValue(["slots", name, "label"]) ?? "",
                  audioLabel:
                    getFieldValue(["slots", name, "audioLabel"]) ?? "",
                  audience: getFieldValue(["slots", name, "audience"]) ?? 5000,
                  rate: getFieldValue(["slots", name, "rate"]) ?? 105000,
                });

                const insertAfter = (i: number) => {
                  const prev = getSlotValues(dayFields[i].name);
                  const insertIndex = fields.indexOf(dayFields[i]) + 1;
                  add(
                    {
                      day,
                      start: prev.end,
                      end: "",
                      label: prev.label,
                      audioLabel: prev.audioLabel,
                      audience: prev.audience,
                      rate: prev.rate,
                    },
                    insertIndex,
                  );
                };

                const addLast = () => {
                  if (dayFields.length > 0) {
                    const prev = getSlotValues(
                      dayFields[dayFields.length - 1].name,
                    );
                    add({
                      day,
                      start: prev.end,
                      end: "",
                      label: prev.label,
                      audioLabel: prev.audioLabel,
                      audience: prev.audience,
                      rate: prev.rate,
                    });
                  } else {
                    const defaults = generateDefaultSlots(platformName);
                    const firstOfDay = defaults.find((s) => s.day === day);
                    add({
                      day,
                      start: firstOfDay?.start ?? "00:00",
                      end: firstOfDay?.end ?? "",
                      label: firstOfDay?.label ?? "",
                      audioLabel: firstOfDay?.audioLabel ?? "",
                      audience: firstOfDay?.audience ?? 5000,
                      rate: firstOfDay?.rate ?? 105000,
                    });
                  }
                };

                const headerStyle: React.CSSProperties = {
                  display: "grid",
                  gridTemplateColumns: "20px 72px 72px 1fr 1fr 80px 90px 32px",
                  gap: 4,
                  padding: "2px 4px",
                  fontSize: 11,
                  fontWeight: 600,
                  color: "#aaa",
                  borderBottom: "1px solid #f0f0f0",
                  marginBottom: 2,
                };

                const rowStyle: React.CSSProperties = {
                  display: "grid",
                  gridTemplateColumns: "20px 72px 72px 1fr 1fr 80px 90px 32px",
                  gap: 4,
                  padding: "2px 4px",
                  alignItems: "center",
                };

                return (
                  <div style={{ marginBottom: 16 }}>
                    <Divider
                      orientation="left"
                      style={{ fontSize: 12, margin: "8px 0" }}
                    >
                      {dayLabel}
                    </Divider>

                    {dayFields.map(({ name }) => (
                      <Form.Item
                        key={`hidden-${name}`}
                        name={[name, "day"]}
                        noStyle
                        hidden
                      >
                        <Input />
                      </Form.Item>
                    ))}

                    {dayFields.length > 0 && (
                      <div style={headerStyle}>
                        <span />
                        <span>Inicio</span>
                        <span>Fin</span>
                        <span>Etiqueta</span>
                        <span>Audio Label</span>
                        <span>Audiencia</span>
                        <span>Tarifa</span>
                        <span />
                      </div>
                    )}

                    {dayFields.map(({ name }, i) => (
                      <div key={name} style={rowStyle}>
                        <Button
                          type="text"
                          size="small"
                          icon={<PlusOutlined />}
                          title="Insertar franja después"
                          style={{ color: "#bbb", padding: 0, minWidth: 20 }}
                          onClick={() => insertAfter(i)}
                        />
                        <Form.Item
                          name={[name, "start"]}
                          noStyle
                          rules={[{ required: true, message: "HH:mm" }]}
                          getValueProps={(v) => ({
                            value: v ? dayjs(v, "HH:mm") : undefined,
                          })}
                          getValueFromEvent={(t) =>
                            t ? t.format("HH:mm") : ""
                          }
                        >
                          <TimePicker
                            size="small"
                            format="HH:mm"
                            placeholder="00:00"
                            allowClear={false}
                            style={{ width: "100%" }}
                          />
                        </Form.Item>
                        <Form.Item
                          name={[name, "end"]}
                          noStyle
                          rules={[{ required: true, message: "HH:mm" }]}
                          getValueProps={(v) => ({
                            value: v ? dayjs(v, "HH:mm") : undefined,
                          })}
                          getValueFromEvent={(t) => {
                            const val = t ? t.format("HH:mm") : "";
                            if (i < dayFields.length - 1) {
                              const nextName = dayFields[i + 1].name;
                              form.setFieldValue(
                                ["slots", nextName, "start"],
                                val,
                              );
                            }
                            return val;
                          }}
                        >
                          <TimePicker
                            size="small"
                            format="HH:mm"
                            placeholder="23:59"
                            allowClear={false}
                            style={{ width: "100%" }}
                          />
                        </Form.Item>
                        <Form.Item
                          name={[name, "label"]}
                          noStyle
                          rules={[{ required: true }]}
                        >
                          <Input size="small" />
                        </Form.Item>
                        <Form.Item
                          name={[name, "audioLabel"]}
                          noStyle
                          rules={[{ required: true }]}
                        >
                          <Input size="small" />
                        </Form.Item>
                        <Form.Item name={[name, "audience"]} noStyle>
                          <Input
                            size="small"
                            type="number"
                            placeholder="5000"
                          />
                        </Form.Item>
                        <Form.Item name={[name, "rate"]} noStyle>
                          <Input
                            size="small"
                            type="number"
                            placeholder="105000"
                          />
                        </Form.Item>
                        <Button
                          size="small"
                          danger
                          type="text"
                          icon={<DeleteOutlined />}
                          onClick={() => remove(name)}
                        />
                      </div>
                    ))}

                    {dayFields.length === 0 && (
                      <Button
                        type="dashed"
                        size="small"
                        icon={<PlusOutlined />}
                        style={{ marginTop: 6 }}
                        onClick={addLast}
                      >
                        Agregar franja
                      </Button>
                    )}
                  </div>
                );
              }}
            </Form.Item>
          ))}
        </>
      )}
    </Form.List>
  );
};

const PlatformModal: React.FC<{
  open: boolean;
  initial: PlatformDto | null;
  mediaTypes: MediaTypeDto[];
  defaultMedia?: string;
  cityOptions: { value: string; label: string }[];
  onClose: () => void;
  onSave: (dto: PlatformDto) => void;
  loading: boolean;
}> = ({
  open,
  initial,
  mediaTypes,
  defaultMedia,
  cityOptions,
  onClose,
  onSave,
  loading,
}) => {
  const [form] = Form.useForm();
  const watchedName: string = Form.useWatch("name", form) ?? "";
  const watchedZone: string = Form.useWatch("zone", form) ?? "";
  const watchedMedia: string = Form.useWatch("media", form) ?? "";
  const isAudioVisual = ["radio", "tv", "television"].includes(
    watchedMedia.toLowerCase(),
  );

  React.useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: initial?.name ?? "",
        url: initial?.url ?? "",
        media: initial?.media ?? defaultMedia ?? mediaTypes[0]?.name ?? "",
        zone: initial?.zone ?? "Nacional",
        city: initial?.city ?? "Nacional",
        slots: initial?.slots ?? generateDefaultSlots(""),
        audience: initial?.audience ?? 5000,
        rate: initial?.rate ?? 105000,
        sourceId: initial?.sourceId ?? "",
        streamUrl: initial?.streamUrl ?? "",
        primaryUrl: initial?.primaryUrl ?? "",
        country: initial?.country ?? "",
        fallbackStreamUrls: initial?.fallbackStreamUrls ?? [],
      });
    }
  }, [open, initial, form, mediaTypes, defaultMedia]);

  // Sync city when zone is Nacional or Internacional
  React.useEffect(() => {
    if (!open || !watchedZone) return;
    if (watchedZone === "Nacional" || watchedZone === "Internacional") {
      form.setFieldValue("city", watchedZone);
    }
  }, [watchedZone, open, form]);

  // Re-generate slot labels when name changes (only for new platforms)
  const prevNameRef = React.useRef("");
  React.useEffect(() => {
    if (!open || initial) return;
    if (watchedName === prevNameRef.current) return;
    prevNameRef.current = watchedName;
    form.setFieldValue("slots", generateDefaultSlots(watchedName));
  }, [watchedName, open, initial, form]);

  const handleOk = async () => {
    const values = await form.validateFields();

    const currentMedia: string = values.media ?? "";
    const currentIsAudioVisual = ["radio", "tv", "television"].includes(
      currentMedia.toLowerCase(),
    );

    const captureFields = {
      sourceId: values.sourceId?.trim() || undefined,
      streamUrl: values.streamUrl?.trim() || undefined,
      primaryUrl: values.primaryUrl?.trim() || undefined,
      country: values.country?.trim() || undefined,
      fallbackStreamUrls: (values.fallbackStreamUrls as string[] | undefined)
        ?.map((u: string) => u?.trim())
        .filter(Boolean),
    };

    if (currentIsAudioVisual) {
      // Normalize slots: ensure numeric fields are numbers and all fields present
      const slots: SlotDto[] = (values.slots ?? []).map(
        (s: SlotDto & { audience?: unknown; rate?: unknown }) => ({
          day: s.day,
          start: s.start,
          end: s.end,
          label: s.label ?? "",
          audioLabel: s.audioLabel ?? "",
          audience:
            s.audience !== undefined && s.audience !== null
              ? Number(s.audience)
              : 5000,
          rate:
            s.rate !== undefined && s.rate !== null ? Number(s.rate) : 105000,
        }),
      );

      // Validate full coverage for each day
      for (const { value: day } of DAY_OPTIONS) {
        const err = validateDayCoverage(slots, day);
        if (err) {
          message.error(err);
          return;
        }
      }

      onSave({
        ...(initial?.id ? { id: initial.id } : {}),
        name: values.name,
        url: values.url ?? "",
        media: values.media,
        zone: values.zone ?? "Nacional",
        city: values.city ?? "Nacional",
        slots,
        ...captureFields,
      });
    } else {
      onSave({
        ...(initial?.id ? { id: initial.id } : {}),
        name: values.name,
        url: values.url ?? "",
        media: values.media,
        zone: values.zone ?? "Nacional",
        city: values.city ?? "Nacional",
        slots: [],
        audience:
          values.audience !== undefined && values.audience !== null
            ? Number(values.audience)
            : 5000,
        rate:
          values.rate !== undefined && values.rate !== null
            ? Number(values.rate)
            : 105000,
        ...captureFields,
      });
    }
  };

  return (
    <Modal
      title={initial ? "Editar Plataforma" : "Nueva Plataforma"}
      open={open}
      onOk={handleOk}
      onCancel={onClose}
      confirmLoading={loading}
      okText="Guardar"
      cancelText="Cancelar"
      destroyOnHidden
      width={900}
    >
      <Form form={form} layout="vertical">
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="name" label="Nombre" rules={[{ required: true }]}>
              <Input />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="media" label="Medio" rules={[{ required: true }]}>
              <Select>
                {mediaTypes.map((mt) => (
                  <Option key={mt.name} value={mt.name!}>
                    {mt.label}
                  </Option>
                ))}
              </Select>
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="url" label="URL Web">
          <Input />
        </Form.Item>
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="zone" label="Zona" rules={[{ required: true }]}>
              <Select>
                {ZONE_OPTIONS.map((z) => (
                  <Option key={z} value={z}>
                    {z}
                  </Option>
                ))}
              </Select>
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="city" label="Ciudad">
              <Select
                showSearch
                filterOption={(input, option) => {
                  const norm = (s: string) =>
                    s
                      .normalize("NFD")
                      .replace(/[\u0300-\u036f]/g, "")
                      .toLowerCase();
                  const labelWords = norm(
                    (option?.label as string) ?? "",
                  ).split(/\s+/);
                  const inputWords = norm(input.trim())
                    .split(/\s+/)
                    .filter(Boolean);
                  let li = 0;
                  for (const iw of inputWords) {
                    while (
                      li < labelWords.length &&
                      !labelWords[li].startsWith(iw)
                    )
                      li++;
                    if (li >= labelWords.length) return false;
                    li++;
                  }
                  return inputWords.length > 0;
                }}
                options={cityOptions}
                placeholder="Nacional"
              />
            </Form.Item>
          </Col>
        </Row>

        <Divider orientation="left" style={{ fontSize: 12, margin: "12px 0 8px" }}>
          Configuraci\u00f3n de captura
        </Divider>
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="sourceId" label="Source ID">
              <Input placeholder="ej. caracol_bogota" />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="country" label="Pa\u00eds">
              <Input placeholder="ej. Colombia" />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="streamUrl" label="Stream URL">
          <Input placeholder="https://..." />
        </Form.Item>
        <Form.Item name="primaryUrl" label="Primary URL (descubrimiento)">
          <Input placeholder="https://..." />
        </Form.Item>
        <Form.Item label="Fallback Stream URLs">
          <Form.List name="fallbackStreamUrls">
            {(fields, { add, remove }) => (
              <>
                {fields.map(({ key, name }) => (
                  <Space key={key} style={{ display: "flex", marginBottom: 4 }} align="baseline">
                    <Form.Item name={name} noStyle rules={[{ required: true, message: "Ingresa la URL" }]}>
                      <Input placeholder="https://..." style={{ width: 420 }} />
                    </Form.Item>
                    <MinusCircleOutlined onClick={() => remove(name)} style={{ color: "#ff4d4f" }} />
                  </Space>
                ))}
                <Button
                  type="dashed"
                  size="small"
                  icon={<PlusOutlined />}
                  onClick={() => add("")}
                >
                  Agregar URL de respaldo
                </Button>
              </>
            )}
          </Form.List>
        </Form.Item>

        {isAudioVisual ? (
          <>
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 12, fontSize: 12 }}
              message="Cada tipo de día debe cubrir de 00:00 a 23:59 sin huecos"
            />
            <SlotsField platformName={watchedName} />
          </>
        ) : (
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item
                name="audience"
                label="Audiencia"
                rules={[
                  { required: true, message: "Ingresa la audiencia" },
                  {
                    type: "number",
                    min: 0,
                    message: "Debe ser un número positivo",
                    transform: Number,
                  },
                ]}
              >
                <Input type="number" min={0} placeholder="5000" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item
                name="rate"
                label="Tarifa"
                rules={[
                  { required: true, message: "Ingresa la tarifa" },
                  {
                    type: "number",
                    min: 0,
                    message: "Debe ser un número positivo",
                    transform: Number,
                  },
                ]}
              >
                <Input type="number" min={0} placeholder="105000" />
              </Form.Item>
            </Col>
          </Row>
        )}
      </Form>
    </Modal>
  );
};

// ─── User Modal ────────────────────────────────────────────────────────────────

const UserModal: React.FC<{
  open: boolean;
  initial: FirebaseUserDto | null;
  onClose: () => void;
  onSave: (dto: FirebaseUserDto) => void;
  loading: boolean;
}> = ({ open, initial, onClose, onSave, loading }) => {
  const [form] = Form.useForm();
  const isEdit = !!initial?.uid;

  React.useEffect(() => {
    if (open) {
      form.setFieldsValue({
        email: initial?.email ?? "",
        displayName: initial?.displayName ?? "",
        role: initial?.role ?? "initial",
        phone: initial?.phone ?? "",
        password: "",
      });
    }
  }, [open, initial, form]);

  const handleOk = async () => {
    const values = await form.validateFields();
    if (!values.password) delete values.password;
    onSave({ ...(initial?.uid ? { uid: initial.uid } : {}), ...values });
  };

  return (
    <Modal
      title={isEdit ? "Editar Usuario" : "Nuevo Usuario"}
      open={open}
      onOk={handleOk}
      onCancel={onClose}
      confirmLoading={loading}
      okText="Guardar"
      cancelText="Cancelar"
      destroyOnHidden
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="email"
          label="Email"
          rules={[{ required: true, type: "email" }]}
        >
          <Input disabled={isEdit} />
        </Form.Item>
        <Form.Item name="displayName" label="Nombre">
          <Input />
        </Form.Item>
        <Form.Item name="role" label="Rol" rules={[{ required: true }]}>
          <Select>
            {ROLE_OPTIONS.map((r) => (
              <Option key={r} value={r}>
                {r}
              </Option>
            ))}
          </Select>
        </Form.Item>
        <Form.Item
          name="phone"
          label="Teléfono (con indicativo de país)"
          rules={[
            {
              pattern: /^\+?[1-9]\d{6,14}$/,
              message: "Formato: +573001234567",
            },
          ]}
        >
          <Input placeholder="+573001234567" />
        </Form.Item>
        {!isEdit && (
          <Form.Item
            name="password"
            label="Contraseña"
            rules={[{ required: true, min: 6 }]}
          >
            <Input.Password />
          </Form.Item>
        )}
      </Form>
    </Modal>
  );
};

// ─── Media Type Modal ─────────────────────────────────────────────────────────

const MediaTypeModal: React.FC<{
  open: boolean;
  initial: MediaTypeDto | null;
  onClose: () => void;
  onSave: (dto: MediaTypeDto) => void;
  loading: boolean;
}> = ({ open, initial, onClose, onSave, loading }) => {
  const [form] = Form.useForm();

  React.useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: initial?.name ?? "",
        label: initial?.label ?? "",
      });
    }
  }, [open, initial, form]);

  const handleOk = async () => {
    const values = await form.validateFields();
    onSave({ ...(initial?.id ? { id: initial.id } : {}), ...values });
  };

  return (
    <Modal
      title={initial ? "Editar Medio" : "Nuevo Medio"}
      open={open}
      onOk={handleOk}
      onCancel={onClose}
      confirmLoading={loading}
      okText="Guardar"
      cancelText="Cancelar"
      destroyOnHidden
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="name"
          label="Clave interna"
          rules={[
            {
              required: true,
              pattern: /^[a-z]+$/,
              message: "Solo letras minúsculas",
            },
          ]}
        >
          <Input placeholder="radio" />
        </Form.Item>
        <Form.Item name="label" label="Etiqueta" rules={[{ required: true }]}>
          <Input placeholder="Radio" />
        </Form.Item>
      </Form>
    </Modal>
  );
};

// ─── Word Adds Field (extracted to reduce nesting depth) ─────────────────────

const WordAddsField: React.FC<{ wordName: number }> = ({ wordName }) => (
  <Form.List name={[wordName, "adds"]}>
    {(addFields, { add: addAdd, remove: removeAdd }) => (
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        {addFields.map(({ key: ak, name: an }) => (
          <Row key={ak} gutter={8} align="middle">
            <Col flex="1">
              <Form.Item name={[an, "before"]} noStyle>
                <Input placeholder="Antes" size="small" />
              </Form.Item>
            </Col>
            <Col flex="1">
              <Form.Item name={[an, "after"]} noStyle>
                <Input placeholder="Después" size="small" />
              </Form.Item>
            </Col>
            <Col>
              <Button
                size="small"
                danger
                icon={<MinusCircleOutlined />}
                onClick={() => removeAdd(an)}
              />
            </Col>
          </Row>
        ))}
        <Button
          size="small"
          type="dashed"
          icon={<PlusOutlined />}
          onClick={() => addAdd({ before: "", after: "" })}
          style={{ alignSelf: "flex-start" }}
        >
          Agregar contexto
        </Button>
      </div>
    )}
  </Form.List>
);

// ─── Client Modal ─────────────────────────────────────────────────────────────

const ClientModal: React.FC<{
  open: boolean;
  initial: ClientDto | null;
  mediaTypes: MediaTypeDto[];
  usersWithPhone: FirebaseUserDto[];
  onClose: () => void;
  onSave: (dto: ClientDto) => void;
  loading: boolean;
}> = ({
  open,
  initial,
  mediaTypes,
  usersWithPhone,
  onClose,
  onSave,
  loading,
}) => {
  const [form] = Form.useForm();

  // Con destroyOnHidden el form se remonta en cada apertura;
  // initialValues se lee durante el montaje, antes de que los Form.List
  // registren sus campos, evitando la carrera de setFieldsValue.
  const formInitialValues = React.useMemo(
    () => ({
      name: initial?.name ?? "",
      words: initial?.words?.length ? initial.words : [{ value: "", adds: [] }],
      topics: initial?.topics ?? [],
      alerts: initial?.alerts ?? {},
      notes: initial?.notes ?? {},
    }),
    [open, initial],
  );

  const handleOk = async () => {
    const values = await form.validateFields();
    const words: WordDto[] = (values.words ?? initial?.words ?? []).filter(
      (w: WordDto) => w?.value?.trim(),
    );
    const topics = (values.topics ?? initial?.topics ?? []).filter((t: any) =>
      t?.name?.trim(),
    );
    onSave({
      ...(initial?.id ? { id: initial.id } : {}),
      name: values.name ?? initial?.name ?? "",
      words,
      topics,
      alerts: values.alerts ?? initial?.alerts ?? {},
      notes: values.notes ?? initial?.notes ?? {},
    });
  };

  const userOptions = usersWithPhone.map((u) => ({
    label: `${u.displayName || u.email} (${u.phone})`,
    value: u.phone!,
  }));

  const contactSection = (
    fieldName: "alerts" | "notes",
    description: string,
  ) => (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <Alert
        message={description}
        type="info"
        showIcon
        style={{ fontSize: 12 }}
      />
      {mediaTypes.map((mt) => (
        <Form.Item
          key={mt.name}
          name={[fieldName, mt.name!]}
          label={mt.label}
          style={{ marginBottom: 8 }}
        >
          <Select
            mode="multiple"
            placeholder="Seleccionar usuarios"
            options={userOptions}
            optionFilterProp="label"
            allowClear
          />
        </Form.Item>
      ))}
    </div>
  );

  const modalTabs = [
    {
      key: "info",
      label: "Información",
      children: (
        <Form.Item
          name="name"
          label="Nombre del cliente"
          rules={[{ required: true }]}
        >
          <Input />
        </Form.Item>
      ),
    },
    {
      key: "words",
      label: "Palabras Clave",
      children: (
        <Form.List name="words">
          {(fields, { add, remove }) => (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {fields.map(({ key, name }) => (
                <Card
                  key={key}
                  size="small"
                  style={{ background: "transparent" }}
                  extra={
                    <Button
                      size="small"
                      danger
                      icon={<MinusCircleOutlined />}
                      onClick={() => remove(name)}
                    />
                  }
                  title={
                    <Text style={{ fontSize: 12 }}>Palabra {name + 1}</Text>
                  }
                >
                  <Form.Item
                    name={[name, "value"]}
                    label="Valor"
                    rules={[
                      { required: true, message: "Escribe la palabra clave" },
                    ]}
                    style={{ marginBottom: 8 }}
                  >
                    <Input placeholder="ej. ecopetrol" />
                  </Form.Item>
                  <Text
                    style={{ fontSize: 12, display: "block", marginBottom: 4 }}
                  >
                    Contexto adicional (antes / después)
                  </Text>
                  <WordAddsField wordName={name} />
                </Card>
              ))}
              <Button
                type="dashed"
                icon={<PlusOutlined />}
                onClick={() => add({ value: "", adds: [] })}
                block
              >
                Agregar palabra clave
              </Button>
            </div>
          )}
        </Form.List>
      ),
    },
    {
      key: "topics",
      label: "Temas",
      children: (
        <>
          <style>
            {`
              .topic-add-line {
                position: relative;
                height: 0;
                margin-top: 10px;
                margin-bottom: 10px;
              }
              .topic-add-btn {
                position: absolute;
                left: 50%;
                top: 0;
                transform: translate(-50%, -50%);
                opacity: 1;
                font-size: 18px;
                line-height: 1;
                padding: 0 !important;
                height: auto !important;
                background: transparent;
              }
            `}
          </style>
          <Form.List name="topics">
            {(topicFields, { add: addTopic, remove: removeTopic }) => (
              <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                {topicFields.map(({ key: topicKey, name: topicName }) => (
                  <div key={topicKey}>
                    {/* Nivel 1 – nombre del tema FUERA del Form.List de subtopics */}
                    <div
                      style={{
                        display: "flex",
                        gap: 6,
                        alignItems: "center",
                        marginBottom: 4,
                      }}
                    >
                      <Form.Item
                        name={[topicName, "name"]}
                        rules={[{ required: true, message: "" }]}
                        style={{ marginBottom: 0, flex: 1 }}
                      >
                        <Input placeholder="Tema" style={{ fontWeight: 600 }} />
                      </Form.Item>
                      <Button
                        size="small"
                        danger
                        icon={<MinusCircleOutlined />}
                        onClick={() => removeTopic(topicName)}
                        title="Eliminar tema"
                      />
                    </div>

                    {/* Form.List de subtopics */}
                    <Form.List name={[topicName, "subtopics"]}>
                      {(subtopicFields, { remove: removeSubtopic }) => (
                        <div
                          style={{
                            marginLeft: 20,
                            paddingLeft: 12,
                            borderLeft: "2px solid #e0e0e0",
                            display: "flex",
                            flexDirection: "column",
                            gap: 4,
                          }}
                        >
                          {subtopicFields.map(
                            ({ key: subtopicKey, name: subtopicName }) => (
                              <div key={subtopicKey}>
                                {/* Nivel 2 – nombre del subtema FUERA del Form.List de subsubtopics */}
                                <div
                                  style={{
                                    display: "flex",
                                    gap: 6,
                                    alignItems: "center",
                                    marginBottom: 4,
                                  }}
                                >
                                  <Form.Item
                                    name={[subtopicName, "name"]}
                                    rules={[{ required: true, message: "" }]}
                                    style={{ marginBottom: 0, flex: 1 }}
                                  >
                                    <Input placeholder="Subtema" />
                                  </Form.Item>
                                  <Button
                                    size="small"
                                    danger
                                    icon={<MinusCircleOutlined />}
                                    onClick={() => removeSubtopic(subtopicName)}
                                    title="Eliminar subtema"
                                  />
                                </div>

                                {/* Form.List de subsubtopics */}
                                <Form.List
                                  name={[subtopicName, "subsubtopics"]}
                                >
                                  {(
                                    subsubtopicFields,
                                    { remove: removeSubsubtopic },
                                  ) => (
                                    <div
                                      style={{
                                        marginLeft: 20,
                                        paddingLeft: 12,
                                        borderLeft: "2px solid #ebebeb",
                                        display: "flex",
                                        flexDirection: "column",
                                        gap: 4,
                                      }}
                                    >
                                      {subsubtopicFields.map(
                                        ({ key: subKey, name: subName }) => (
                                          <div
                                            key={subKey}
                                            style={{
                                              display: "flex",
                                              gap: 6,
                                              alignItems: "center",
                                            }}
                                          >
                                            <Form.Item
                                              name={[subName, "name"]}
                                              rules={[
                                                { required: true, message: "" },
                                              ]}
                                              style={{
                                                marginBottom: 0,
                                                flex: 1,
                                              }}
                                            >
                                              <Input
                                                placeholder="Subsubtema"
                                                size="small"
                                              />
                                            </Form.Item>
                                            <Button
                                              size="small"
                                              danger
                                              icon={<MinusCircleOutlined />}
                                              onClick={() =>
                                                removeSubsubtopic(subName)
                                              }
                                              title="Eliminar subsubtema"
                                            />
                                          </div>
                                        ),
                                      )}
                                    </div>
                                  )}
                                </Form.List>
                                <div className="topic-add-line">
                                  <Button
                                    type="link"
                                    size="small"
                                    icon={<PlusOutlined />}
                                    onClick={() => {
                                      const current =
                                        (form.getFieldValue([
                                          "topics",
                                          topicName,
                                          "subtopics",
                                          subtopicName,
                                          "subsubtopics",
                                        ]) as Array<{ name?: string }>) ?? [];
                                      form.setFieldValue(
                                        [
                                          "topics",
                                          topicName,
                                          "subtopics",
                                          subtopicName,
                                          "subsubtopics",
                                        ],
                                        [...current, { name: "" }],
                                      );
                                    }}
                                    className="topic-add-btn"
                                    style={{ padding: 0, height: "auto" }}
                                    title="Agregar subsubtema"
                                  />
                                </div>
                              </div>
                            ),
                          )}
                        </div>
                      )}
                    </Form.List>
                    <div className="topic-add-line">
                      <Button
                        type="link"
                        size="small"
                        icon={<PlusOutlined />}
                        onClick={() => {
                          const current =
                            (form.getFieldValue([
                              "topics",
                              topicName,
                              "subtopics",
                            ]) as Array<{
                              name?: string;
                              subsubtopics?: Array<{ name?: string }>;
                            }>) ?? [];
                          form.setFieldValue(
                            ["topics", topicName, "subtopics"],
                            [...current, { name: "", subsubtopics: [] }],
                          );
                        }}
                        className="topic-add-btn"
                        style={{ padding: 0, height: "auto" }}
                        title="Agregar subtema"
                      />
                    </div>
                  </div>
                ))}
                <Button
                  type="dashed"
                  icon={<PlusOutlined />}
                  onClick={() => addTopic({ name: "", subtopics: [] })}
                  block
                  style={{ marginTop: 8 }}
                >
                  + Agregar tema
                </Button>
              </div>
            )}
          </Form.List>
        </>
      ),
    },
    {
      key: "alerts",
      label: "Alertas",
      children: contactSection(
        "alerts",
        "Usuarios que recibirán alertas por cada medio.",
      ),
    },

    {
      key: "notes",
      label: "Notas",
      children: contactSection(
        "notes",
        "Usuarios que recibirán notas por cada medio.",
      ),
    },
  ];

  return (
    <Modal
      title={initial ? "Editar Cliente" : "Nuevo Cliente"}
      open={open}
      onOk={handleOk}
      onCancel={onClose}
      confirmLoading={loading}
      okText="Guardar"
      cancelText="Cancelar"
      destroyOnHidden
      width={640}
    >
      <Form form={form} layout="vertical" initialValues={formInitialValues}>
        <Tabs items={modalTabs} size="small" destroyInactiveTabPane={false} />
      </Form>
    </Modal>
  );
};

// ─── Main Page ────────────────────────────────────────────────────────────────

const SettingsPage: React.FC = () => {
  const queryClient = useQueryClient();

  // ── Platforms state ───────────────────────────────────
  const [selectedMedia, setSelectedMedia] = useState<string>("");
  const [platformModal, setPlatformModal] = useState<{
    open: boolean;
    data: PlatformDto | null;
  }>({ open: false, data: null });

  // ── Users state ───────────────────────────────────────
  const [userModal, setUserModal] = useState<{
    open: boolean;
    data: FirebaseUserDto | null;
  }>({ open: false, data: null });

  // ── Clients state ─────────────────────────────────────
  const [clientModal, setClientModal] = useState<{
    open: boolean;
    data: ClientDto | null;
  }>({ open: false, data: null });

  // ── Media type state ──────────────────────────────────
  const [mediaModal, setMediaModal] = useState<{
    open: boolean;
    data: MediaTypeDto | null;
  }>({ open: false, data: null });

  // ── Queries ────────────────────────────────────────────

  const { data: platforms = [], isLoading: loadingPlatforms } = useQuery(
    ["platforms", selectedMedia],
    async () => {
      const res = await api.get(`/settings/get-platforms/${selectedMedia}`);
      return res.data as PlatformDto[];
    },
  );

  const { data: users = [], isLoading: loadingUsers } = useQuery(
    ["firebase-users"],
    async () => {
      const res = await api.get("/settings/users");
      return res.data as FirebaseUserDto[];
    },
  );

  const { data: clients = [], isLoading: loadingClients } = useQuery(
    ["clients"],
    async () => {
      const res = await api.get("/clients");
      return res.data as ClientDto[];
    },
  );

  const { data: mediaTypes = [], isLoading: loadingMediaTypes } = useQuery(
    ["media-types"],
    async () => {
      const res = await api.get("/clients/media/all");
      return res.data as MediaTypeDto[];
    },
  );

  const { data: colombiaCities = STATIC_CITY_OPTIONS } = useQuery(
    ["colombia-cities"],
    async () => {
      const res = await fetch(
        "https://api-colombia.com/api/v1/City?pageNumber=1&pageSize=2000",
      );
      if (!res.ok) throw new Error("Failed to fetch cities");
      const json: { id: number; name: string }[] = await res.json();
      const items = Array.isArray(json) ? json : [];
      const sorted = [...items]
        .filter((c) => c.name)
        .sort((a, b) => a.name.localeCompare(b.name, "es"));
      const unique = sorted.filter(
        (c, i, arr) => i === 0 || c.name !== arr[i - 1].name,
      );
      return [
        ...STATIC_CITY_OPTIONS,
        ...unique.map((c) => ({ value: c.name, label: c.name })),
      ];
    },
    { staleTime: Infinity, cacheTime: Infinity, retry: 2 },
  );

  // Set default selectedMedia once mediaTypes loads
  React.useEffect(() => {
    if (mediaTypes.length > 0 && !selectedMedia) {
      setSelectedMedia(mediaTypes[0].name ?? "");
    }
  }, [mediaTypes, selectedMedia]);

  const usersWithPhone = users.filter(
    (u) => u.phone && /^\+?[1-9]\d{6,14}$/.test(u.phone),
  );

  // ── Platform mutations ────────────────────────────────

  const savePlatform = useMutation(
    async (dto: PlatformDto) => {
      if (dto.id)
        return (await api.post("/settings/update-platform", dto)).data;
      return (await api.post("/settings/create-platform", dto)).data;
    },
    {
      onSuccess: () => {
        message.success("Plataforma guardada");
        queryClient.invalidateQueries(["platforms", selectedMedia]);
        setPlatformModal({ open: false, data: null });
      },
      onError: () => {
        message.error("Error al guardar la plataforma");
      },
    },
  );

  // ── User mutations ────────────────────────────────────

  const saveUser = useMutation(
    async (dto: FirebaseUserDto) =>
      (await api.post("/settings/users/update", dto)).data,
    {
      onSuccess: () => {
        message.success("Usuario guardado");
        queryClient.invalidateQueries(["firebase-users"]);
        setUserModal({ open: false, data: null });
      },
      onError: () => {
        message.error("Error al guardar el usuario");
      },
    },
  );

  const deleteUser = useMutation(
    async (uid: string) => (await api.delete(`/settings/users/${uid}`)).data,
    {
      onSuccess: () => {
        message.success("Usuario eliminado");
        queryClient.invalidateQueries(["firebase-users"]);
      },
      onError: () => {
        message.error("Error al eliminar el usuario");
      },
    },
  );

  // ── Client mutations ──────────────────────────────────

  const saveClient = useMutation(
    async (dto: ClientDto) => {
      if (dto.id)
        return (await api.put(`/clients/${dto.id}`, dto)).data as ClientDto;
      return (await api.post("/clients", dto)).data as ClientDto;
    },
    {
      onSuccess: (saved: ClientDto, dto: ClientDto) => {
        message.success("Cliente guardado");
        // Actualizar caché directamente para evitar estado vacío durante el refetch
        queryClient.setQueryData<ClientDto[]>(["clients"], (prev = []) => {
          if (dto.id) {
            return prev.map((c) => (c.id === dto.id ? saved : c));
          }
          return [...prev, saved];
        });
        queryClient.invalidateQueries(["clients"]);
        setClientModal({ open: false, data: null });
      },
      onError: () => {
        message.error("Error al guardar el cliente");
      },
    },
  );

  const deleteClient = useMutation(
    async (id: string) => (await api.delete(`/clients/${id}`)).data,
    {
      onSuccess: () => {
        message.success("Cliente eliminado");
        queryClient.invalidateQueries(["clients"]);
      },
      onError: () => {
        message.error("Error al eliminar el cliente");
      },
    },
  );

  // ── Media type mutations ───────────────────────────────

  const saveMediaType = useMutation(
    async (dto: MediaTypeDto) => {
      if (dto.id) return (await api.put(`/clients/media/${dto.id}`, dto)).data;
      return (await api.post("/clients/media", dto)).data;
    },
    {
      onSuccess: () => {
        message.success("Medio guardado");
        queryClient.invalidateQueries(["media-types"]);
        setMediaModal({ open: false, data: null });
      },
      onError: () => {
        message.error("Error al guardar el medio");
      },
    },
  );

  const deleteMediaType = useMutation(
    async (id: string) => (await api.delete(`/clients/media/${id}`)).data,
    {
      onSuccess: () => {
        message.success("Medio eliminado");
        queryClient.invalidateQueries(["media-types"]);
      },
      onError: () => {
        message.error("Error al eliminar el medio");
      },
    },
  );

  const deletePlatform = useMutation(
    async (id: string) =>
      (await api.delete(`/settings/delete-platform/${id}`)).data,
    {
      onSuccess: () => {
        message.success("Plataforma eliminada");
        queryClient.invalidateQueries(["platforms", selectedMedia]);
      },
      onError: () => {
        message.error("Error al eliminar la plataforma");
      },
    },
  );

  // ── Platform columns ──────────────────────────────────

  const platformColumns = [
    { title: "Nombre", dataIndex: "name", key: "name" },
    { title: "Medio", dataIndex: "media", key: "media", width: 80 },
    { title: "Zona", dataIndex: "zone", key: "zone", width: 120 },
    { title: "Ciudad", dataIndex: "city", key: "city", width: 100 },
    {
      title: "URL",
      dataIndex: "url",
      key: "url",
      ellipsis: true,
      render: (url: string) =>
        url ? (
          <a
            href={url}
            target="_blank"
            rel="noopener noreferrer"
            style={{ fontSize: 12 }}
          >
            {url}
          </a>
        ) : (
          "-"
        ),
    },
    {
      title: "Franjas",
      key: "slots",
      width: 80,
      render: (_: unknown, record: PlatformDto) => (
        <Tag>{record.slots?.length ?? 0} franja(s)</Tag>
      ),
    },
    {
      title: "",
      key: "actions",
      width: 80,
      render: (_: unknown, record: PlatformDto) => (
        <Space>
          <Button
            icon={<EditOutlined />}
            size="small"
            onClick={(e) => {
              e.stopPropagation();
              setPlatformModal({ open: true, data: record });
            }}
          />
          <Popconfirm
            title="¿Eliminar plataforma?"
            okText="Sí"
            cancelText="No"
            onConfirm={() => record.id && deletePlatform.mutate(record.id)}
          >
            <Button
              icon={<DeleteOutlined />}
              size="small"
              danger
              onClick={(e) => e.stopPropagation()}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const userColumns = [
    {
      title: "Email",
      dataIndex: "email",
      key: "email",
      render: (email: string) => (
        <Space>
          <UserOutlined style={{ color: "#1890ff" }} />
          <Text>{email}</Text>
        </Space>
      ),
    },
    {
      title: "Nombre",
      dataIndex: "displayName",
      key: "displayName",
      render: (v: string) => v || <Text type="secondary">—</Text>,
    },
    {
      title: "Teléfono",
      dataIndex: "phone",
      key: "phone",
      render: (v: string) => v || <Text type="secondary">—</Text>,
    },
    {
      title: "Rol",
      dataIndex: "role",
      key: "role",
      width: 90,
      render: (role: string) => {
        const roleColorMap: Record<string, string> = {
          admin: "red",
          user: "blue",
          initial: "orange",
        };
        return (
          <>
            <Tag color={roleColorMap[role] ?? "default"}>{role ?? "—"}</Tag>
            {role === "initial" && (
              <Alert
                message="Comuníquese con el administrador para activar su cuenta"
                type="warning"
                showIcon
                banner
                style={{ fontSize: 11, marginTop: 4 }}
              />
            )}
          </>
        );
      },
    },
    {
      title: "Estado",
      dataIndex: "disabled",
      key: "disabled",
      width: 80,
      render: (disabled: boolean) =>
        disabled ? (
          <Tag color="orange">Inactivo</Tag>
        ) : (
          <Tag color="green">Activo</Tag>
        ),
    },
    {
      title: "",
      key: "actions",
      width: 80,
      render: (_: unknown, record: FirebaseUserDto) => (
        <Space>
          <Button
            icon={<EditOutlined />}
            size="small"
            onClick={(e) => {
              e.stopPropagation();
              setUserModal({ open: true, data: record });
            }}
          />
          <Popconfirm
            title="¿Eliminar usuario?"
            okText="Sí"
            cancelText="No"
            onConfirm={() => record.uid && deleteUser.mutate(record.uid)}
          >
            <Button
              icon={<DeleteOutlined />}
              size="small"
              danger
              onClick={(e) => e.stopPropagation()}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  // ── Client columns ────────────────────────────────────

  const clientColumns = [
    { title: "Nombre", dataIndex: "name", key: "name" },
    {
      title: "Palabras clave",
      dataIndex: "words",
      key: "words",
      render: (words: WordDto[]) =>
        words?.length ? (
          <Space size={[4, 4]} wrap>
            {words.slice(0, 5).map((w) => (
              <Tag key={w.value}>{w.value}</Tag>
            ))}
            {words.length > 5 && <Tag>+{words.length - 5}</Tag>}
          </Space>
        ) : (
          <Text type="secondary">—</Text>
        ),
    },
    {
      title: "Alertas",
      key: "alerts",
      render: (_: unknown, record: ClientDto) => {
        const total = Object.values(record.alerts ?? {}).reduce(
          (acc, arr) => acc + ((arr as string[])?.length ?? 0),
          0,
        );
        return total ? (
          <Tag color="blue">{total} contacto(s)</Tag>
        ) : (
          <Text type="secondary">—</Text>
        );
      },
    },
    {
      title: "Notas",
      key: "notes",
      render: (_: unknown, record: ClientDto) => {
        const total = Object.values(record.notes ?? {}).reduce(
          (acc, arr) => acc + ((arr as string[])?.length ?? 0),
          0,
        );
        return total ? (
          <Tag color="purple">{total} contacto(s)</Tag>
        ) : (
          <Text type="secondary">—</Text>
        );
      },
    },
    {
      title: "",
      key: "actions",
      width: 80,
      render: (_: unknown, record: ClientDto) => (
        <Space>
          <Button
            icon={<EditOutlined />}
            size="small"
            onClick={(e) => {
              e.stopPropagation();
              setClientModal({ open: true, data: record });
            }}
          />
          <Popconfirm
            title="¿Eliminar cliente?"
            okText="Sí"
            cancelText="No"
            onConfirm={() => record.id && deleteClient.mutate(record.id)}
          >
            <Button
              icon={<DeleteOutlined />}
              size="small"
              danger
              onClick={(e) => e.stopPropagation()}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  // ── Media type columns ────────────────────────────────

  const mediaColumns = [
    {
      title: "Clave",
      dataIndex: "name",
      key: "name",
      width: 120,
      render: (v: string) => <Tag>{v}</Tag>,
    },
    { title: "Etiqueta", dataIndex: "label", key: "label" },
    {
      title: "",
      key: "actions",
      width: 80,
      render: (_: unknown, record: MediaTypeDto) => (
        <Space>
          <Button
            icon={<EditOutlined />}
            size="small"
            onClick={(e) => {
              e.stopPropagation();
              setMediaModal({ open: true, data: record });
            }}
          />
          <Popconfirm
            title="¿Eliminar medio?"
            okText="Sí"
            cancelText="No"
            onConfirm={() => record.id && deleteMediaType.mutate(record.id)}
          >
            <Button
              icon={<DeleteOutlined />}
              size="small"
              danger
              onClick={(e) => e.stopPropagation()}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  // ── Tabs content ──────────────────────────────────────

  const tabItems = [
    {
      key: "users",
      label: "Usuarios",
      children: (
        <Card size="small" bordered={false}>
          <Table
            rowKey="uid"
            dataSource={users}
            columns={userColumns}
            loading={loadingUsers}
            pagination={false}
            size="small"
            scroll={{ y: 400 }}
          />
        </Card>
      ),
    },
    {
      key: "clients",
      label: "Clientes",
      children: (
        <Card
          size="small"
          bordered={false}
          extra={
            <Button
              type="primary"
              icon={<PlusOutlined />}
              size="small"
              onClick={() => setClientModal({ open: true, data: null })}
            >
              Nuevo
            </Button>
          }
        >
          <Table
            rowKey="id"
            dataSource={clients}
            columns={clientColumns}
            loading={loadingClients}
            pagination={false}
            size="small"
            scroll={{ y: 400 }}
          />
        </Card>
      ),
    },
    {
      key: "media",
      label: "Medios",
      children: (
        <Card
          size="small"
          bordered={false}
          extra={
            <Button
              type="primary"
              icon={<PlusOutlined />}
              size="small"
              onClick={() => setMediaModal({ open: true, data: null })}
            >
              Nuevo
            </Button>
          }
        >
          <Table
            rowKey="id"
            dataSource={mediaTypes}
            columns={mediaColumns}
            loading={loadingMediaTypes}
            pagination={false}
            size="small"
            scroll={{ y: 400 }}
          />
        </Card>
      ),
    },
    {
      key: "platforms",
      label: "Plataformas",
      children: (
        <Card
          size="small"
          title={
            <Space>
              <Title level={5} style={{ margin: 0 }}>
                Plataformas
              </Title>
              <Select
                value={selectedMedia}
                onChange={(v) => setSelectedMedia(v)}
                style={{ width: 120 }}
                size="small"
              >
                {mediaTypes.map((mt) => (
                  <Option key={mt.name} value={mt.name!}>
                    {mt.label}
                  </Option>
                ))}
              </Select>
            </Space>
          }
          extra={
            <Button
              type="primary"
              icon={<PlusOutlined />}
              size="small"
              onClick={() => setPlatformModal({ open: true, data: null })}
            >
              Nueva
            </Button>
          }
          bordered={false}
        >
          <Table
            rowKey="id"
            dataSource={platforms}
            columns={platformColumns}
            loading={loadingPlatforms}
            pagination={false}
            size="small"
            scroll={{ y: 400 }}
          />
        </Card>
      ),
    },
  ];

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <Tabs defaultActiveKey="users" items={tabItems} />

      {/* ── Modals ──────────────────────────────── */}
      <PlatformModal
        open={platformModal.open}
        initial={platformModal.data}
        mediaTypes={mediaTypes}
        defaultMedia={selectedMedia}
        cityOptions={colombiaCities}
        onClose={() => setPlatformModal({ open: false, data: null })}
        onSave={(dto) => savePlatform.mutate(dto)}
        loading={savePlatform.isLoading}
      />
      <UserModal
        open={userModal.open}
        initial={userModal.data}
        onClose={() => setUserModal({ open: false, data: null })}
        onSave={(dto) => saveUser.mutate(dto)}
        loading={saveUser.isLoading}
      />
      <ClientModal
        open={clientModal.open}
        initial={clientModal.data}
        mediaTypes={mediaTypes}
        usersWithPhone={usersWithPhone}
        onClose={() => setClientModal({ open: false, data: null })}
        onSave={(dto) => saveClient.mutate(dto)}
        loading={saveClient.isLoading}
      />
      <MediaTypeModal
        open={mediaModal.open}
        initial={mediaModal.data}
        onClose={() => setMediaModal({ open: false, data: null })}
        onSave={(dto) => saveMediaType.mutate(dto)}
        loading={saveMediaType.isLoading}
      />
    </div>
  );
};

export default SettingsPage;
