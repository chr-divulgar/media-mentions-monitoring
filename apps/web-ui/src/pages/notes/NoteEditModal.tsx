import React from "react";
import { Modal, Form, Input, Row, Col } from "antd";
import { NoteDto } from "@repo/shared/index";

interface NoteEditModalProps {
  open: boolean;
  note: NoteDto | null;
  onSave: (note: NoteDto) => void;
  onCancel: () => void;
}

const NoteEditModal: React.FC<NoteEditModalProps> = ({
  open,
  note,
  onSave,
  onCancel,
}) => {
  const [form] = Form.useForm();

  React.useEffect(() => {
    if (note) {
      form.setFieldsValue(note);
    }
  }, [note, form]);

  const handleOk = () => {
    form.validateFields().then((values) => {
      onSave({ ...note, ...values });
    });
  };

  return (
    <Modal
      open={open}
      title="Editar Nota"
      onOk={handleOk}
      onCancel={onCancel}
      okText="Guardar"
      cancelText="Cancelar"
      width={800}
    >
      <Form form={form} layout="vertical">
        <Row gutter={16}>
          <Col span={12}>
            <Form.Item
              name="date"
              label="Fecha"
              rules={[
                { required: true, message: "El campo Fecha es obligatorio" },
              ]}
            >
              <Input />
            </Form.Item>
            <Form.Item
              name="media"
              label="Medio"
              rules={[
                { required: true, message: "El campo Medio es obligatorio" },
              ]}
            >
              <Input />
            </Form.Item>
            <Form.Item
              name="mediaName"
              label="Nombre del Medio"
              rules={[
                {
                  required: true,
                  message: "El campo Nombre del Medio es obligatorio",
                },
              ]}
            >
              <Input />
            </Form.Item>
            <Form.Item
              name="title"
              label="Título"
              rules={[
                { required: true, message: "El campo Título es obligatorio" },
              ]}
            >
              <Input />
            </Form.Item>

            <Form.Item name="summary" label="Resumen">
              <Input.TextArea rows={3} />
            </Form.Item>
            <Form.Item name="sentiment" label="Sentimiento">
              <Input />
            </Form.Item>
            <Form.Item name="value" label="Valor">
              <Input />
            </Form.Item>
            <Form.Item name="audience" label="Audiencia">
              <Input />
            </Form.Item>
            <Form.Item name="link" label="Link">
              <Input />
            </Form.Item>
            <Form.Item name="source" label="Fuente">
              <Input />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="variables" label="Variables">
              <Input />
            </Form.Item>
            <Form.Item name="topic" label="Tema">
              <Input />
            </Form.Item>
            <Form.Item name="subtopics" label="Subtemas">
              <Input />
            </Form.Item>
            <Form.Item name="origin" label="Origen">
              <Input />
            </Form.Item>
            <Form.Item name="department" label="Departamento">
              <Input />
            </Form.Item>
            <Form.Item name="zone" label="Zona">
              <Input />
            </Form.Item>
            <Form.Item name="rate" label="Tarifa">
              <Input />
            </Form.Item>
            <Form.Item name="program" label="Programa">
              <Input />
            </Form.Item>
            <Form.Item name="platform" label="Plataforma">
              <Input />
            </Form.Item>
            <Form.Item name="clientName" label="Cliente">
              <Input />
            </Form.Item>
          </Col>
        </Row>
      </Form>
    </Modal>
  );
};

export default NoteEditModal;
