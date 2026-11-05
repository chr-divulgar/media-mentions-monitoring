import React from "react";
import { Button, Form, Input, Space, Typography } from "antd";
import { MinusCircleOutlined, PlusOutlined } from "@ant-design/icons";

const FEED_PREFIX = "https://www.google.com/alerts/feeds/";

// The client's Google Alerts RSS feeds: the worker reads them every 15 minutes, next to the Google News search. Alerts
// search the whole text of the pages, so they bring notes that name the client only in the body.
const ClientGoogleAlertsSettings: React.FC = () => (
  <>
    <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>
      Crea la alerta en google.com/alerts (por ejemplo «Ecopetrol») y en «Mostrar opciones» elige: Frecuencia «A medida
      que se produce», Fuentes «Automático», Idioma «Español», Región «Cualquier región», Cantidad «Todos los
      resultados» y Enviar a «Feed RSS». Luego copia el enlace del ícono RSS y pégalo aquí. Una línea por alerta.
    </Typography.Paragraph>
    <Form.List name="googleAlertsFeeds">
      {(fields, { add, remove }) => (
        <>
          {fields.map(({ key, name }) => (
            <Space key={key} align="baseline" style={{ display: "flex", marginBottom: 8 }}>
              <Form.Item
                name={name}
                rules={[
                  { required: true, message: "Pega el enlace del feed" },
                  {
                    validator: (_, value?: string) =>
                      !value || value.trim().startsWith(FEED_PREFIX)
                        ? Promise.resolve()
                        : Promise.reject(new Error(`Debe empezar por ${FEED_PREFIX}`)),
                  },
                ]}
                style={{ marginBottom: 0 }}
              >
                <Input placeholder={`${FEED_PREFIX}…`} style={{ width: 560 }} />
              </Form.Item>
              <MinusCircleOutlined onClick={() => remove(name)} />
            </Space>
          ))}
          <Button type="dashed" icon={<PlusOutlined />} onClick={() => add("")}>
            Agregar feed
          </Button>
        </>
      )}
    </Form.List>
  </>
);

export default ClientGoogleAlertsSettings;
