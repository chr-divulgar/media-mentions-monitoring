import React, { useState } from "react";
import {
  DatePicker,
  Button,
  Row,
  Col,
  Upload,
  Modal,
  Checkbox,
  message,
} from "antd";
import { UploadOutlined } from "@ant-design/icons";
import * as XLSX from "xlsx";

const NotesPage: React.FC = () => {
  const [selectedDates, setSelectedDates] = useState<any>(null);
  const [sheetNames, setSheetNames] = useState<string[]>([]);
  const [selectedSheets, setSelectedSheets] = useState<string[]>([]);
  const [modalVisible, setModalVisible] = useState(false);

  const handleDateChange = (dates: any) => {
    setSelectedDates(dates);
  };

  const handleUpload = (file: File) => {
    const isExcel =
      file.type ===
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ||
      file.name.endsWith(".xlsx");
    if (!isExcel) {
      message.error("Solo se permiten archivos Excel (.xlsx)");
      return Upload.LIST_IGNORE;
    }
    const reader = new FileReader();
    reader.onload = (e) => {
      const data = new Uint8Array(e.target?.result as ArrayBuffer);
      const workbook = XLSX.read(data, { type: "array" });
      setSheetNames(workbook.SheetNames);
      setSelectedSheets([]);
      setModalVisible(true);
    };
    reader.readAsArrayBuffer(file);
    return false; // Prevenir carga automática
  };

  const handleSheetSelect = (checkedValues: any) => {
    setSelectedSheets(checkedValues);
  };

  const handleModalOk = () => {
    // Aquí puedes manejar las hojas seleccionadas
    setModalVisible(false);
    message.success(`Hojas seleccionadas: ${selectedSheets.join(", ")}`);
  };

  const uploadProps = {
    name: "file",
    accept: ".xlsx",
    showUploadList: false,
    beforeUpload: handleUpload,
  };

  return (
    <div>
      <Row align="middle" justify="space-between" style={{ marginBottom: 24 }}>
        <Col>
          <DatePicker.RangePicker
            value={selectedDates}
            onChange={handleDateChange}
            style={{ minWidth: 240 }}
            allowClear={false}
          />
        </Col>
        <Col>
          <Upload {...uploadProps}>
            <Button type="primary" icon={<UploadOutlined />}>
              Cargar XLSX
            </Button>
          </Upload>
        </Col>
      </Row>
      <Modal
        title="Selecciona las hojas a cargar"
        open={modalVisible}
        onOk={handleModalOk}
        onCancel={() => setModalVisible(false)}
        okButtonProps={{ disabled: selectedSheets.length === 0 }}
      >
        <Checkbox.Group value={selectedSheets} onChange={handleSheetSelect}>
          <Row gutter={[16, 8]}>
            {sheetNames.map((name) => (
              <Col span={8} key={name} style={{ marginBottom: 8 }}>
                <Checkbox value={name}>
                  <span
                    style={{
                      display: "inline-block",
                      maxWidth: 120,
                      overflow: "hidden",
                      textOverflow: "ellipsis",
                      whiteSpace: "nowrap",
                      verticalAlign: "middle",
                      cursor: "pointer",
                    }}
                    title={name}
                  >
                    {name}
                  </span>
                </Checkbox>
              </Col>
            ))}
          </Row>
        </Checkbox.Group>
      </Modal>
      <h1>Notas</h1>
      <p>Aquí irá el contenido de la página de notas.</p>
    </div>
  );
};

export default NotesPage;
