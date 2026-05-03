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
} from "antd";
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { useQuery, useMutation, useQueryClient } from "react-query";
import api from "../../services/Agent";
import type { PlatformDto } from "@repo/shared";

const { Title, Text } = Typography;
const { Option } = Select;

const MEDIA_OPTIONS = ["radio", "TV", "Prensa", "Digital"];
const ROLE_OPTIONS = ["admin", "editor", "viewer"];

// ─── Types ────────────────────────────────────────────────────────────────────

interface FirebaseUserDto {
  uid?: string;
  email: string;
  displayName?: string;
  password?: string;
  role?: string;
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

// ─── User form ────────────────────────────────────────────────────────────────

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
        role: initial?.role ?? ROLE_OPTIONS[2],
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
          name="password"
          label={isEdit ? "Nueva contraseña (opcional)" : "Contraseña"}
          rules={isEdit ? [] : [{ required: true, min: 6 }]}
        >
          <Input.Password />
        </Form.Item>
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

  // ── Platform mutations ────────────────────────────────

  const savePlatform = useMutation(
    async (dto: PlatformDto) => {
      if (dto.id) {
        return (await api.post("/settings/update-platform", dto)).data;
      }
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
    async (dto: FirebaseUserDto) => {
      if (dto.uid) {
        return (await api.post("/settings/users/update", dto)).data;
      }
      return (await api.post("/settings/users/create", dto)).data;
    },
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
    async (uid: string) => {
      return (await api.delete(`/settings/users/${uid}`)).data;
    },
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
      title: "Rol",
      dataIndex: "role",
      key: "role",
      width: 90,
      render: (role: string) => {
        const color =
          role === "admin" ? "red" : role === "editor" ? "blue" : "default";
        return <Tag color={color}>{role ?? "—"}</Tag>;
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

  // ── Tabs content ──────────────────────────────────────

  const tabItems = [
    {
      key: "users",
      label: "Usuarios",
      children: (
        <Card
          size="small"
          extra={
            <Button
              type="primary"
              icon={<PlusOutlined />}
              size="small"
              onClick={() => setUserModal({ open: true, data: null })}
            >
              Nuevo
            </Button>
          }
          bordered={false}
        >
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
        <Card size="small" bordered={false}>
          <div
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              height: 200,
            }}
          >
            <Text type="secondary">Próximamente…</Text>
          </div>
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
    </div>
  );
};

export default SettingsPage;
