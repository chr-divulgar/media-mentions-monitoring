import React, { useEffect, useState } from "react";
import { Alert, Button, Col, Form, Input, message, Modal, Row, Spin, Typography, Upload } from "antd";
import { UploadOutlined } from "@ant-design/icons";
import { AxiosError } from "axios";
import dayjs from "dayjs";
import { useMutation, useQueryClient } from "react-query";
import { InternetNotePreviewDto, NoteDto, NoteOrigin, NoteSentiment } from "@repo/shared/index";
import api from "../../services/Agent";
import { useAlert } from "./AlertsContext";
import AlertNoteContext from "../notes/noteFields/AlertNoteContext";
import ClassificationFields from "../notes/noteFields/ClassificationFields";
import ProgramAndRateFields from "../notes/noteFields/ProgramAndRateFields";
import { useNoteFields } from "../notes/noteFields/useNoteFields";

interface InternetNoteModalProps {
  visible: boolean;
  onClose: () => void;
}

// Every field of the note's form that is saved (the alert's own data is sent by the server).
type FormValues = Pick<
  NoteDto,
  | "index"
  | "program"
  | "title"
  | "summary"
  | "topic"
  | "subtopic"
  | "subsubtopic"
  | "origin"
  | "sentiment"
  | "zone"
  | "city"
  | "rate"
  | "audience"
  | "value"
>;

// A PDF the user makes by hand (the page could not be printed or verified) is limited like the server's.
const MAX_PDF_BYTES = 25 * 1024 * 1024;

// The API returns the real failure reason (often the worker's own, in Spanish) as `{ error }`.
const describeError = (error: unknown, fallback: string): string =>
  (error as AxiosError<{ error?: string }>)?.response?.data?.error ?? fallback;

// Editing an internet alert: there is no audio or video to cut. Opening it makes the worker print
// the article's page to a PDF, shown here next to the form together with the link it will have.
// Nothing is archived until "Guardar": only then does the PDF go to the server and the note get
// written, with that link read-only. The note carries the same fields as a manual one.
//
// When the page could not be verified (an alert left to "Revisar manualmente") or could not be printed,
// the user can make the PDF themselves and upload it: it takes the place of the printed one and is
// archived the same way when the note is saved.
const InternetNoteModal: React.FC<InternetNoteModalProps> = ({ visible, onClose }) => {
  const { selectedAlert } = useAlert();
  const queryClient = useQueryClient();
  const [form] = Form.useForm();
  const [pdfUrl, setPdfUrl] = useState<string>();
  const [pdfError, setPdfError] = useState<string>();
  const [noteId, setNoteId] = useState<string>();
  // The preview made from the user's own PDF; when there is one it is the one that is saved.
  const [manualPreview, setManualPreview] = useState<InternetNotePreviewDto>();
  const noteFields = useNoteFields(form, { noteId, lockContext: true, indexForAllMedia: true });

  // Shows a preview (the printed one or the user's own PDF): the form with what the note already has, and the PDF.
  const applyPreview = async (preview: InternetNotePreviewDto) => {
    const saved = preview.note;
    // The alert's own data first (read-only), then what an earlier save of this note holds.
    const published = dayjs.utc(selectedAlert?.startTime).utcOffset(-300);
    setNoteId(saved.id);
    form.setFieldsValue({
      clientName: selectedAlert?.clientName,
      media: "internet",
      mediaName: selectedAlert?.platform,
      date: published,
      startTime: published,
      index: saved.index,
      program: saved.program,
      title: saved.title,
      summary: saved.summary,
      topic: saved.topic,
      subtopic: saved.subtopic,
      subsubtopic: saved.subsubtopic,
      origin: saved.origin ?? NoteOrigin.DIRECTA,
      sentiment: saved.sentiment ?? NoteSentiment.NEUTRO,
      zone: saved.zone,
      city: saved.city,
      rate: saved.rate,
      audience: saved.audience,
      value: saved.value,
    });
    setPdfError(undefined);
    try {
      const pdf = await api.get(`/notes/internet-pdf/preview/${preview.previewId}`, { responseType: "blob" });
      setPdfUrl(URL.createObjectURL(pdf.data));
    } catch (error) {
      setPdfError(describeError(error, "No se pudo mostrar el PDF."));
    }
  };

  const openPreview = useMutation<InternetNotePreviewDto, unknown, string>(
    async (alertId) => (await api.post("/notes/internet-pdf/preview", { alertId })).data,
    { onSuccess: applyPreview },
  );

  const uploadPdf = useMutation<InternetNotePreviewDto, unknown, File>(
    async (file) =>
      (
        await api.post(`/notes/internet-pdf/upload?alertId=${encodeURIComponent(selectedAlert?.id ?? "")}`, file, {
          headers: { "Content-Type": "application/pdf" },
        })
      ).data,
    {
      onSuccess: async (uploaded) => {
        setManualPreview(uploaded);
        await applyPreview(uploaded);
        message.success("PDF subido: se archivará al guardar la nota");
      },
      onError: (error) => {
        message.error(describeError(error, "No se pudo subir el PDF"));
      },
    },
  );

  const saveNote = useMutation<unknown, unknown, FormValues>(
    async (values) =>
      (
        await api.post("/notes/internet-pdf/save", {
          alertId: selectedAlert?.id,
          previewId: preview?.previewId,
          ...values,
        })
      ).data,
    {
      onSuccess: () => {
        message.success("Nota guardada y PDF archivado");
        queryClient.invalidateQueries(["notes"]);
        onClose();
      },
      onError: (error) => {
        message.error(describeError(error, "Error al guardar la nota"));
      },
    },
  );

  useEffect(() => {
    // Only when the modal opens for an alert, not on every mutation state change.
    if (visible && selectedAlert?.id) {
      setManualPreview(undefined);
      openPreview.mutate(selectedAlert.id);
    }
  }, [visible, selectedAlert?.id]);

  // The viewer's blob URL holds the whole PDF in memory: release it when replaced or closed.
  useEffect(() => () => {
    if (pdfUrl) URL.revokeObjectURL(pdfUrl);
  }, [pdfUrl]);

  const preview = manualPreview ?? openPreview.data;
  // The alert could not be verified, or its page could not be printed: offer the user's own PDF.
  const canUploadPdf = !!selectedAlert?.reviewReason || openPreview.isError;

  const beforeUpload = (file: File) => {
    if (file.type !== "application/pdf" && !file.name.toLowerCase().endsWith(".pdf")) {
      message.error("El archivo debe ser un PDF");
    } else if (file.size > MAX_PDF_BYTES) {
      message.error("El PDF pesa más de 25 MB");
    } else {
      uploadPdf.mutate(file);
    }
    return false; // never uploaded by the component itself
  };

  const handleOk = async () => {
    saveNote.mutate(await form.validateFields());
  };

  return (
    <Modal
      title="Editar nota de internet"
      open={visible}
      onCancel={onClose}
      onOk={handleOk}
      okText="Guardar"
      cancelText="Cancelar"
      okButtonProps={{ disabled: !preview, loading: saveNote.isLoading }}
      destroyOnHidden
      width={1280}
    >
      <Spin spinning={openPreview.isLoading || uploadPdf.isLoading} tip="Generando vista previa del PDF…">
        {selectedAlert?.reviewReason && (
          <Alert type="warning" showIcon message={`Revisar manualmente: ${selectedAlert.reviewReason}`} style={{ marginBottom: 16 }} />
        )}
        {openPreview.isError && !manualPreview && (
          <Alert
            type="error"
            showIcon
            message={describeError(openPreview.error, "No se pudo generar el PDF de la nota.")}
            action={
              <Button size="small" onClick={() => selectedAlert?.id && openPreview.mutate(selectedAlert.id)}>
                Reintentar
              </Button>
            }
            style={{ marginBottom: 16 }}
          />
        )}
        <Row gutter={24} style={{ minHeight: 480 }}>
          <Col xs={24} md={12} style={{ maxHeight: "70vh", overflowY: "auto" }}>
            <Form form={form} layout="vertical" disabled={!preview}>
              <AlertNoteContext />
              <ProgramAndRateFields
                isRadioOrTelevision={false}
                programOptions={noteFields.programOptions}
                showLink={false}
              />
              <ClassificationFields
                topicOptions={noteFields.topicOptions}
                subtopicOptions={noteFields.subtopicOptions}
                subsubtopicOptions={noteFields.subsubtopicOptions}
                disableSubtopic={noteFields.disableSubtopic}
                disableSubsubtopic={noteFields.disableSubsubtopic}
              />
              <Form.Item label="Titular" name="title" rules={[{ required: true }]}>
                <Input />
              </Form.Item>
              <Form.Item label="Resumen" name="summary" rules={[{ required: true }]}>
                <Input.TextArea autoSize={{ minRows: 4, maxRows: 12 }} />
              </Form.Item>
              <Form.Item label="Enlace del artículo">
                {(preview?.note.link ?? selectedAlert?.url) ? (
                  <Typography.Link
                    href={preview?.note.link ?? selectedAlert?.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    ellipsis
                  >
                    {preview?.note.link ?? selectedAlert?.url}
                  </Typography.Link>
                ) : null}
              </Form.Item>
              <Form.Item
                label="Enlace que tendrá el PDF"
                extra="Se sube al servidor de archivo al guardar; hasta entonces el enlace aún no abre."
              >
                <Input value={preview?.pdfLink ?? ""} disabled placeholder="Se calcula al generar la vista previa" />
              </Form.Item>
            </Form>
          </Col>
          <Col xs={24} md={12}>
            <Typography.Text type="secondary">Vista previa del PDF</Typography.Text>
            {canUploadPdf && (
              <div style={{ marginTop: 8 }}>
                <Upload accept=".pdf,application/pdf" showUploadList={false} beforeUpload={beforeUpload} maxCount={1}>
                  <Button icon={<UploadOutlined />} loading={uploadPdf.isLoading}>
                    {manualPreview ? "Reemplazar el PDF subido" : "Subir PDF hecho a mano"}
                  </Button>
                </Upload>
                <Typography.Text type="secondary" style={{ marginLeft: 8 }}>
                  Si no se pudo generar o verificar, sube aquí el PDF de la nota (máx. 25 MB).
                </Typography.Text>
              </div>
            )}
            {pdfError ? (
              <Alert type="warning" showIcon message={pdfError} style={{ marginTop: 8 }} />
            ) : (
              <iframe
                title="Vista previa del PDF"
                src={pdfUrl}
                style={{ width: "100%", height: 560, border: "1px solid #d9d9d9", borderRadius: 6, marginTop: 8 }}
              />
            )}
          </Col>
        </Row>
      </Spin>
    </Modal>
  );
};

export default InternetNoteModal;
