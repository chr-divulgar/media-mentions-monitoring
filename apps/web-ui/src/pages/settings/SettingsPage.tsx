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
} from "antd";
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
} from "@repo/shared";

const { Title, Text } = Typography;
const { Option } = Select;

const MEDIA_OPTIONS = ["radio", "TV", "Prensa", "Digital"];
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

// ─── Platform form ────────────────────────────────────────────────────────────

const PlatformModal: React.FC<{
  open: boolean;
  initial: PlatformDto | null;
  onClose: () => void;
  onSave: (dto: PlatformDto) => void;
  loading: boolean;
}> = ({ open, initial, onClose, onSave, loading }) => {
  const [form] = Form.useForm();

  React.useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: initial?.name ?? "",
        url: initial?.url ?? "",
        media: initial?.media ?? MEDIA_OPTIONS[0],
      });
    }
  }, [open, initial, form]);

  const handleOk = async () => {
    const values = await form.validateFields();
    onSave({ ...(initial?.id ? { id: initial.id } : {}), ...values });
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
    >
      <Form form={form} layout="vertical">
        <Form.Item name="name" label="Nombre" rules={[{ required: true }]}>
          <Input />
        </Form.Item>
        <Form.Item name="url" label="URL Stream">
          <Input />
        </Form.Item>
        <Form.Item name="media" label="Medio" rules={[{ required: true }]}>
          <Select>
            {MEDIA_OPTIONS.map((m) => (
              <Option key={m} value={m}>
                {m}
              </Option>
            ))}
          </Select>
        </Form.Item>
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

  React.useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: initial?.name ?? "",
        words: initial?.words?.length
          ? initial.words
          : [{ value: "", adds: [] }],
        alerts: initial?.alerts ?? {},
        notes: initial?.notes ?? {},
      });
    }
  }, [open, initial, form]);

  const handleOk = async () => {
    const values = await form.validateFields();
    const words: WordDto[] = (values.words ?? []).filter((w: WordDto) =>
      w?.value?.trim(),
    );
    onSave({
      ...(initial?.id ? { id: initial.id } : {}),
      name: values.name,
      words,
      alerts: values.alerts ?? {},
      notes: values.notes ?? {},
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
      <Form form={form} layout="vertical">
        <Tabs items={modalTabs} size="small" destroyInactiveTabPane={false} />
      </Form>
    </Modal>
  );
};

// ─── Main Page ────────────────────────────────────────────────────────────────

const SettingsPage: React.FC = () => {
  const queryClient = useQueryClient();

  // ── Platforms state ───────────────────────────────────
  const [selectedMedia, setSelectedMedia] = useState<string>(MEDIA_OPTIONS[0]);
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
      if (dto.id) return (await api.put(`/clients/${dto.id}`, dto)).data;
      return (await api.post("/clients", dto)).data;
    },
    {
      onSuccess: () => {
        message.success("Cliente guardado");
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

  // ── Platform columns ──────────────────────────────────

  const platformColumns = [
    { title: "Nombre", dataIndex: "name", key: "name" },
    { title: "Medio", dataIndex: "media", key: "media", width: 80 },
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
      title: "",
      key: "actions",
      width: 50,
      render: (_: unknown, record: PlatformDto) => (
        <Button
          icon={<EditOutlined />}
          size="small"
          onClick={(e) => {
            e.stopPropagation();
            setPlatformModal({ open: true, data: record });
          }}
        />
      ),
    },
  ];

  // ── User columns ──────────────────────────────────────

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
                {MEDIA_OPTIONS.map((m) => (
                  <Option key={m} value={m}>
                    {m}
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
