import React from "react";
import { Button, Form, Input, Space, Typography, Upload, message } from "antd";
import { DeleteOutlined, MinusCircleOutlined, PlusOutlined, UploadOutlined } from "@ant-design/icons";

const MAX_LOGO_BYTES = 500 * 1024;

interface LogoInputProps {
  value?: string;
  onChange?: (value: string) => void;
}

// The logo as a data URL (it is kept in the client's document): a preview, and buttons to change it or take it off.
const LogoInput: React.FC<LogoInputProps> = ({ value, onChange }) => {
  const beforeUpload = (file: File) => {
    if (!/^image\/(png|jpe?g)$/i.test(file.type)) {
      message.error("El logo debe ser una imagen PNG o JPG");
    } else if (file.size > MAX_LOGO_BYTES) {
      message.error("El logo pesa más de 500 KB");
    } else {
      const reader = new FileReader();
      reader.onload = () => onChange?.(String(reader.result));
      reader.readAsDataURL(file);
    }
    return false;
  };

  return (
    <Space direction="vertical">
      {value ? (
        <img src={value} alt="Logo del informe" style={{ maxWidth: 240, maxHeight: 100, border: "1px solid #f0f0f0", padding: 4 }} />
      ) : (
        <Typography.Text type="secondary">Sin logo: el informe sale sin encabezado.</Typography.Text>
      )}
      <Space>
        <Upload accept=".png,.jpg,.jpeg,image/png,image/jpeg" showUploadList={false} beforeUpload={beforeUpload}>
          <Button icon={<UploadOutlined />}>{value ? "Cambiar logo" : "Subir logo"}</Button>
        </Upload>
        {value && (
          <Button icon={<DeleteOutlined />} onClick={() => onChange?.("")}>
            Quitar
          </Button>
        )}
      </Space>
    </Space>
  );
};

// What the client's daily news report (Notas → "Informe diario") carries besides the notes: the logo that heads every
// page and the "Indicadores" lines ("El Tiempo Divisas", "La República Movimiento accionario"...).
const ClientReportSettings: React.FC = () => (
  <>
    <Form.Item name="reportLogo" label="Logo del encabezado (PNG o JPG, máx. 500 KB)">
      <LogoInput />
    </Form.Item>
    <Typography.Text strong>Indicadores</Typography.Text>
    <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>
      Aparecen al inicio del informe, antes de las temáticas. El enlace es opcional.
    </Typography.Paragraph>
    <Form.List name="reportIndicators">
      {(fields, { add, remove }) => (
        <>
          {fields.map(({ key, name }) => (
            <Space key={key} align="baseline" style={{ display: "flex", marginBottom: 8 }}>
              <Form.Item name={[name, "source"]} rules={[{ required: true, message: "Fuente" }]} style={{ marginBottom: 0 }}>
                <Input placeholder="Fuente (El Tiempo)" style={{ width: 170 }} />
              </Form.Item>
              <Form.Item name={[name, "label"]} rules={[{ required: true, message: "Texto" }]} style={{ marginBottom: 0 }}>
                <Input placeholder="Texto (Divisas)" style={{ width: 200 }} />
              </Form.Item>
              <Form.Item name={[name, "url"]} rules={[{ type: "url", message: "Enlace no válido" }]} style={{ marginBottom: 0 }}>
                <Input placeholder="Enlace (opcional)" style={{ width: 260 }} />
              </Form.Item>
              <MinusCircleOutlined onClick={() => remove(name)} />
            </Space>
          ))}
          <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({ source: "", label: "" })}>
            Agregar indicador
          </Button>
        </>
      )}
    </Form.List>
  </>
);

export default ClientReportSettings;
