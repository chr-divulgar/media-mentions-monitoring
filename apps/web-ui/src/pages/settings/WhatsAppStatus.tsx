import React from "react";
import { Card, Typography, Space, Spin, Alert, Badge, Image } from "antd";
import {
  CheckCircleOutlined,
  QrcodeOutlined,
  DisconnectOutlined,
} from "@ant-design/icons";
import { useQuery } from "react-query";
import api from "../../services/Agent";
import type { WhatsAppStatusDto, WhatsAppQrDto } from "@repo/shared";

const { Title, Paragraph } = Typography;

// Same worker-is-the-source-of-truth pattern as YouTubeSettings.tsx: every poll is a fresh
// live call, nothing is cached here or in web-api. QR is intentionally never persisted anywhere
// on this side — it's fetched again each time it's needed, following Baileys' own rotation.
export const WhatsAppStatus: React.FC = () => {
  const { data: status, isLoading: statusLoading } = useQuery(
    "whatsappStatus",
    async () => {
      const response = await api.get("/settings/whatsapp/status");
      return response.data as WhatsAppStatusDto;
    },
    {
      refetchInterval: 30000,
      staleTime: 10000,
    }
  );

  const isPendingQr = status?.status === "pending_qr";

  const { data: qr, isLoading: qrLoading } = useQuery(
    "whatsappQr",
    async () => {
      const response = await api.get("/settings/whatsapp/qr");
      return response.data as WhatsAppQrDto;
    },
    {
      enabled: isPendingQr,
      refetchInterval: isPendingQr ? 5000 : false,
    }
  );

  const renderStatusBadge = () => {
    if (!status) return null;
    if (status.status === "connected") {
      return <Badge status="success" text={<span><CheckCircleOutlined /> Conectado</span>} />;
    }
    if (status.status === "pending_qr") {
      return <Badge status="processing" text={<span><QrcodeOutlined /> Esperando escaneo de QR</span>} />;
    }
    if (status.status === "worker_unreachable") {
      return <Badge status="default" text={<span><DisconnectOutlined /> Worker no disponible</span>} />;
    }
    return <Badge status="error" text="Desconectado" />;
  };

  return (
    <div style={{ padding: "24px" }}>
      <Space direction="vertical" style={{ width: "100%" }} size="large">
        <div>
          <Title level={2}>WhatsApp</Title>
          <Paragraph>
            Estado de la sesión de WhatsApp usada para enviar notificaciones de alertas. La
            sesión vive en el worker (sidecar Baileys); esta página solo consulta su estado, no
            guarda nada localmente.
          </Paragraph>
        </div>

        {statusLoading ? (
          <Spin />
        ) : (
          <Card>
            {renderStatusBadge()}

            {status?.status === "worker_unreachable" && (
              <Alert
                style={{ marginTop: 16 }}
                message="Worker no disponible"
                description="No se pudo contactar al worker. El estado de WhatsApp no se puede verificar en este momento."
                type="warning"
                showIcon
              />
            )}

            {isPendingQr && (
              <div style={{ marginTop: 16, textAlign: "center" }}>
                {qrLoading || !qr?.qr ? (
                  <Spin />
                ) : (
                  <>
                    <Paragraph>
                      Escaneá este código desde WhatsApp → Dispositivos vinculados → Vincular
                      dispositivo.
                    </Paragraph>
                    <Image src={qr.qr} alt="Código QR de WhatsApp" width={256} preview={false} />
                  </>
                )}
              </div>
            )}
          </Card>
        )}
      </Space>
    </div>
  );
};

export default WhatsAppStatus;
