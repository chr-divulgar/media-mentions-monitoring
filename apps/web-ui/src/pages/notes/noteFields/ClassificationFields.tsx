import React from "react";
import { Col, Form, Radio, Row, Select } from "antd";
import { NoteOrigin, NoteSentiment } from "@repo/shared/index";

interface Option {
  value?: string;
  label?: string;
}

interface ClassificationFieldsProps {
  topicOptions: Option[];
  subtopicOptions: Option[];
  subsubtopicOptions: Option[];
  disableSubtopic: boolean;
  disableSubsubtopic: boolean;
}

// Tema → Subtema → Subsubtema (the client's own topics; the cascade is kept consistent by
// useNoteFields) plus the note's Origen and Sentimiento.
const ClassificationFields: React.FC<ClassificationFieldsProps> = ({
  topicOptions,
  subtopicOptions,
  subsubtopicOptions,
  disableSubtopic,
  disableSubsubtopic,
}) => (
  <>
    <Row gutter={12}>
      <Col span={8}>
        <Form.Item name="topic" label="Tema">
          <Select allowClear placeholder="Tema" options={topicOptions} />
        </Form.Item>
      </Col>
      <Col span={8}>
        <Form.Item name="subtopic" label="Subtema">
          <Select allowClear placeholder="Subtema" options={subtopicOptions} disabled={disableSubtopic} />
        </Form.Item>
      </Col>
      <Col span={8}>
        <Form.Item name="subsubtopic" label="Subsubtema">
          <Select allowClear placeholder="Subsubtema" options={subsubtopicOptions} disabled={disableSubsubtopic} />
        </Form.Item>
      </Col>
    </Row>
    <Row gutter={12}>
      <Col span={10}>
        <Form.Item name="origin" label="Origen">
          <Radio.Group>
            <Radio.Button value={NoteOrigin.DIRECTA}>{NoteOrigin.DIRECTA}</Radio.Button>
            <Radio.Button value={NoteOrigin.INDIRECTA}>{NoteOrigin.INDIRECTA}</Radio.Button>
          </Radio.Group>
        </Form.Item>
      </Col>
      <Col span={14}>
        <Form.Item name="sentiment" label="Sentimiento">
          <Radio.Group>
            <Radio.Button value={NoteSentiment.NEUTRO}>{NoteSentiment.NEUTRO}</Radio.Button>
            <Radio.Button value={NoteSentiment.POSITIVO}>{NoteSentiment.POSITIVO}</Radio.Button>
            <Radio.Button value={NoteSentiment.NEGATIVO}>{NoteSentiment.NEGATIVO}</Radio.Button>
          </Radio.Group>
        </Form.Item>
      </Col>
    </Row>
  </>
);

export default ClassificationFields;
