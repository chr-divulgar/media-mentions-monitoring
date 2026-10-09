import React from "react";
import { Col, DatePicker, Form, Input, Row, TimePicker } from "antd";

// What an alert already knows about its note: client, medium, outlet, date, time and the outlet's
// zone and city. Read-only fields, but real form fields: the shared note logic (useNoteFields)
// reads them (the medium and outlet pick the program slot, the date and time pick the slot's
// day and hour) and the editors send them with the note.
const AlertNoteContext: React.FC = () => (
  <Row gutter={12}>
    <Col span={12}>
      <Form.Item name="clientName" label="Cliente">
        <Input disabled />
      </Form.Item>
    </Col>
    <Col span={12}>
      <Form.Item name="media" label="Medio">
        <Input disabled />
      </Form.Item>
    </Col>
    <Col span={12}>
      <Form.Item name="mediaName" label="Nombre del medio">
        <Input disabled />
      </Form.Item>
    </Col>
    <Col span={6}>
      <Form.Item name="date" label="Fecha">
        <DatePicker disabled format="DD/MM/YYYY" style={{ width: "100%" }} />
      </Form.Item>
    </Col>
    <Col span={6}>
      <Form.Item name="startTime" label="Hora">
        <TimePicker disabled format="HH:mm" style={{ width: "100%" }} />
      </Form.Item>
    </Col>
    <Col span={12}>
      <Form.Item name="zone" label="Zona">
        <Input disabled />
      </Form.Item>
    </Col>
    <Col span={12}>
      <Form.Item name="city" label="Ciudad">
        <Input disabled />
      </Form.Item>
    </Col>
  </Row>
);

export default AlertNoteContext;
