import React from "react";
import { Col, Form, Input, Row, Select } from "antd";

interface ProgramAndRateFieldsProps {
  isRadioOrTelevision: boolean;
  programOptions: Array<{ value: string; label: string }>;
  // Hidden where the editor shows the link itself (a web note's is the article's, not typed).
  showLink?: boolean;
}

// NOTA, program, audio label, rate, audience, value and link — the fields a note's price depends
// on. Radio and television pick the program slot nearest to the time and everything else follows
// from it (read-only); other media type the program and may adjust rate, audience and value.
// The values are filled by useNoteFields.
const ProgramAndRateFields: React.FC<ProgramAndRateFieldsProps> = ({ isRadioOrTelevision, programOptions, showLink = true }) => (
  <>
    <Row gutter={12}>
      <Col span={6}>
        <Form.Item
          name="index"
          label="NOTA"
          rules={[
            { required: true, message: "Falta el consecutivo" },
            { pattern: /^\d{2,}$/, message: "Debe ser numérico, de al menos 2 dígitos" },
          ]}
        >
          <Input disabled placeholder="##" />
        </Form.Item>
      </Col>
      <Col span={18}>
        <Form.Item name="program" label="Programa" rules={[{ required: true, message: "Selecciona el programa" }]}>
          {isRadioOrTelevision ? (
            <Select placeholder="Seleccione un programa..." options={programOptions} />
          ) : (
            <Input placeholder="Programa o sección" />
          )}
        </Form.Item>
      </Col>
    </Row>
    {isRadioOrTelevision && (
      <Form.Item name="audioLabel" label="Audio">
        <Input disabled />
      </Form.Item>
    )}
    <Row gutter={12}>
      <Col span={8}>
        <Form.Item name="rate" label="Tarifa">
          <Input disabled={isRadioOrTelevision} />
        </Form.Item>
      </Col>
      <Col span={8}>
        <Form.Item name="audience" label="Audiencia">
          <Input disabled={isRadioOrTelevision} />
        </Form.Item>
      </Col>
      <Col span={8}>
        <Form.Item name="value" label="Valor">
          <Input disabled={isRadioOrTelevision} />
        </Form.Item>
      </Col>
    </Row>
    {showLink && (
      <Form.Item name="link" label="Link">
        <Input placeholder="https://..." />
      </Form.Item>
    )}
  </>
);

export default ProgramAndRateFields;
