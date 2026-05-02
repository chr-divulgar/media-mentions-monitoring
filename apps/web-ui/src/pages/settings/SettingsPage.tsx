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
} from "antd";
import { PlusOutlined, EditOutlined } from "@ant-design/icons";
import { useQuery, useMutation, useQueryClient } from "react-query";
import api from "../../services/Agent";
import type { PlatformDto } from "@repo/shared";

const { Title } = Typography;
const { Option } = Select;

const MEDIA_OPTIONS = ["radio", "TV", "Prensa", "Digital"];

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

// ─── Main Page ────────────────────────────────────────────────────────────────

const SettingsPage: React.FC = () => {
  const queryClient = useQueryClient();

  const [selectedMedia, setSelectedMedia] = useState<string>(MEDIA_OPTIONS[0]);
  const [platformModal, setPlatformModal] = useState<{
    open: boolean;
    data: PlatformDto | null;
  }>({ open: false, data: null });

  // ── Queries ────────────────────────────────────────────

  const { data: platforms = [], isLoading: loadingPlatforms } = useQuery(
    ["platforms", selectedMedia],
    async () => {
      const res = await api.get(`/settings/get-platforms/${selectedMedia}`);
      return res.data as PlatformDto[];
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

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      {/* ── Platforms panel ─────────────────────── */}
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

      {/* ── Modals ──────────────────────────────── */}
      <PlatformModal
        open={platformModal.open}
        initial={platformModal.data}
        onClose={() => setPlatformModal({ open: false, data: null })}
        onSave={(dto) => savePlatform.mutate(dto)}
        loading={savePlatform.isLoading}
      />
    </div>
  );
};

export default SettingsPage;
