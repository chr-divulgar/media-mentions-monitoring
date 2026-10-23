import React, { useEffect, useState } from "react";
import { DatePicker, Form, Modal, Select, message } from "antd";
import dayjs, { Dayjs } from "dayjs";
import { useQuery } from "react-query";
import api from "../../services/Agent";

interface DailyReportModalProps {
  open: boolean;
  onClose: () => void;
  // The range chosen on the Notes page: the report starts from it.
  defaultRange?: [Dayjs, Dayjs] | null;
}

// What the API answers when there is nothing to report comes back as a Blob (the request asks for one).
async function errorMessage(error: unknown): Promise<string> {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (data instanceof Blob) {
    try {
      const parsed = JSON.parse(await data.text()) as { error?: string; message?: string };
      return parsed.error ?? parsed.message ?? "No se pudo generar el informe";
    } catch {
      return "No se pudo generar el informe";
    }
  }
  return "No se pudo generar el informe";
}

// The client's daily news report (.docx): its internet notes of the chosen days, grouped by subtopic, with the logo
// and indicators set in Configuración → Clientes → "Informe diario".
const DailyReportModal: React.FC<DailyReportModalProps> = ({ open, onClose, defaultRange }) => {
  const [form] = Form.useForm();
  const [generating, setGenerating] = useState(false);

  const { data: clients = [] } = useQuery<{ name: string }[]>(
    ["clients"],
    async () => (await api.get("/clients")).data,
    { staleTime: 5 * 60 * 1000 },
  );

  useEffect(() => {
    if (open) {
      form.setFieldsValue({ range: defaultRange ?? [dayjs().subtract(1, "day"), dayjs()] });
    }
  }, [open, defaultRange, form]);

  const generate = async () => {
    const { client, range } = await form.validateFields();
    const [from, to] = (range as [Dayjs, Dayjs]).map((day) => day.format("YYYY-MM-DD"));
    setGenerating(true);
    try {
      const response = await api.get("/notes/daily-report", {
        params: { client, from, to },
        responseType: "blob",
      });
      const disposition = String(response.headers["content-disposition"] ?? "");
      const named = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
      const fileName = named
        ? decodeURIComponent(named)
        : `Informe ${client}-${dayjs(to).format("DD-MM-YY")}.docx`;

      const url = URL.createObjectURL(response.data as Blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      anchor.click();
      URL.revokeObjectURL(url);
      onClose();
    } catch (error) {
      message.error(await errorMessage(error));
    } finally {
      setGenerating(false);
    }
  };

  return (
    <Modal
      title="Informe diario de noticias"
      open={open}
      onOk={generate}
      onCancel={onClose}
      okText="Generar y descargar"
      cancelText="Cancelar"
      confirmLoading={generating}
      destroyOnHidden
    >
      <Form form={form} layout="vertical">
        <Form.Item name="client" label="Cliente" rules={[{ required: true, message: "Elige el cliente" }]}>
          <Select
            showSearch
            placeholder="Cliente"
            options={clients.map((client) => ({ value: client.name, label: client.name }))}
          />
        </Form.Item>
        <Form.Item name="range" label="Fechas de las notas" rules={[{ required: true, message: "Elige las fechas" }]}>
          <DatePicker.RangePicker allowClear={false} style={{ width: "100%" }} />
        </Form.Item>
      </Form>
    </Modal>
  );
};

export default DailyReportModal;
