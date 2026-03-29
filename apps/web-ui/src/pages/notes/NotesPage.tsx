import React, { useState, useRef, useEffect } from "react";
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
import dayjs from "dayjs";
import isoWeek from "dayjs/plugin/isoWeek";
dayjs.extend(isoWeek);
import api from "../../services/Agent";
import { useQuery } from "react-query";
import NotesTable from "./NotesTable";
import NoteEditModal from "./NoteEditModal";

const NotesPage: React.FC = () => {
  // Por defecto, seleccionar la semana del último dato (maxDate)
  const [selectedDates, setSelectedDates] = useState<any>(null);
  const [minDate, setMinDate] = useState<string | null>(null);
  const [maxDate, setMaxDate] = useState<string | null>(null);

  // Estado para modal de nueva nota
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [addNote, setAddNote] = useState<any>(null);

  // Guardar nueva nota
  const handleAddNote = async (note: any) => {
    try {
      await api.post("/notes/import-excel", [note]);
      message.success("Nota agregada correctamente");
      setAddModalOpen(false);
      refetch();
    } catch (err) {
      message.error("Error al agregar la nota");
    }
  };
  // Consultar min/max date al montar
  useEffect(() => {
    const fetchDates = async () => {
      try {
        const res = await api.post("/notes/dates", {});
        setMinDate(res.data.minDate);
        setMaxDate(res.data.maxDate);
        if (res.data.maxDate) {
          const max = dayjs(res.data.maxDate, "YYYY-MM-DD");
          setSelectedDates([max.startOf("isoWeek"), max.endOf("isoWeek")]);
        }
      } catch (err) {
        setMinDate(null);
        setMaxDate(null);
      }
    };
    fetchDates();
  }, []);
  const [sheetNames, setSheetNames] = useState<string[]>([]);
  const [selectedSheets, setSelectedSheets] = useState<string[]>([]);
  const [modalVisible, setModalVisible] = useState(false);
  const workbookRef = useRef<XLSX.WorkBook | null>(null);
  const [loading, setLoading] = useState(false);

  // Consultar notas por rango de fechas
  const {
    data: notes,
    isLoading: isLoadingNotes,
    error: errorNotes,
    refetch,
  } = useQuery(
    ["notes", selectedDates],
    async () => {
      if (!selectedDates || !selectedDates[0] || !selectedDates[1]) return [];
      const res = await api.post("/notes/list", {
        startDate: selectedDates[0].format("YYYY-MM-DD"),
        endDate: selectedDates[1].format("YYYY-MM-DD"),
      });
      return res.data;
    },
    { enabled: !!selectedDates && !!selectedDates[0] && !!selectedDates[1] },
  );

  const handleDateChange = (dates: any) => {
    setSelectedDates(dates);
    // Refrescar notas al cambiar fechas
    refetch();
  };

  // Función para seleccionar una semana completa al hacer clic en un día
  const handleCalendarChange = (dates: any) => {
    if (Array.isArray(dates) && dates[0] && !dates[1]) {
      // Cuando el usuario selecciona solo un día, selecciona la semana completa
      const start = dayjs(dates[0]).startOf("isoWeek");
      const end = dayjs(dates[0]).endOf("isoWeek");
      setSelectedDates([start, end]);
    }
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
      workbookRef.current = workbook;
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

  const columnMap: Record<string, string> = {
    Feha: "date",
    "TIPO DE MEDIO": "media",
    Medio: "mediaName",
    VARIABLES: "variables",
    Tema: "topic",
    Subtemas: "subtopics",
    Origen: "origin",
    DEPARTAMENTO: "department",
    Zona: "zone",
    Título: "title",
    RESUMEN: "summary",
    TARIFA: "rate",
    Sentimiento: "sentiment",
    VALOR: "value",
    Audiencia: "audience",
    LINK: "link",
    FUENTE: "source",
  };

  const handleModalOk = async () => {
    if (!workbookRef.current) return;
    setLoading(true);
    const notes: any[] = [];
    selectedSheets.forEach((sheetName) => {
      const ws = workbookRef.current!.Sheets[sheetName];
      const json: any[] = XLSX.utils.sheet_to_json(ws, { defval: "" });
      json.forEach((row, rowIdx) => {
        const note: Record<string, any> = {};
        Object.entries(columnMap).forEach(([col, key]) => {
          let value = row[col] ?? "";
          // Si la columna es fecha, formatear a YYYY-MM-DD
          if (key === "date" && value) {
            if (typeof value === "number") {
              const utcDays = Math.floor(value);
              const utcDate = new Date(
                Date.UTC(1899, 11, 30) + (utcDays + 1) * 86400000,
              );
              value = dayjs(utcDate).format("YYYY-MM-DD");
            } else {
              const d = dayjs(value);
              value = d.isValid() ? d.format("YYYY-MM-DD") : value;
            }
          }
          // Si la columna es LINK, buscar el hipervínculo real si existe
          if (key === "link") {
            // Buscar el hipervínculo en la hoja de Excel
            const ws = workbookRef.current!.Sheets[sheetName];
            const cellAddress = XLSX.utils.encode_cell({
              r: rowIdx + 1,
              c: Object.keys(row).indexOf(col),
            });
            const cell = ws[cellAddress];
            if (cell && cell.l && cell.l.Target) {
              value = cell.l.Target;
            }
          }
          note[key] = value;
        });
        notes.push(note);
      });
    });
    try {
      await api.post("/notes/import-excel", notes);
      message.success("Notas importadas correctamente");
    } catch (err) {
      message.error("Error al importar las notas");
    }
    setLoading(false);
    setModalVisible(false);
  };

  const uploadProps = {
    name: "file",
    accept: ".xlsx",
    showUploadList: false,
    beforeUpload: handleUpload,
  };

  function getCurrentWeek(): any {
    const today = dayjs();
    return [today.startOf("isoWeek"), today.endOf("isoWeek")];
  }

  return (
    <div>
      <Row align="middle" justify="space-between" style={{ marginBottom: 24 }}>
        <Col>
          <DatePicker.RangePicker
            value={selectedDates}
            onChange={handleDateChange}
            style={{ minWidth: 240 }}
            allowClear={false}
            // Limitar fechas habilitadas según min/max
            disabledDate={(current) => {
              if (!minDate || !maxDate) return false;
              const min = dayjs(minDate, "YYYY-MM-DD");
              const max = dayjs(maxDate, "YYYY-MM-DD");
              return current < min || current > max;
            }}
            // Permitir seleccionar por semana
            renderExtraFooter={() => (
              <Button
                size="small"
                onClick={() => setSelectedDates(getCurrentWeek())}
                style={{ width: "100%" }}
              >
                Seleccionar semana actual
              </Button>
            )}
            // Al seleccionar un día, seleccionar la semana completa
            onCalendarChange={handleCalendarChange}
          />
        </Col>
        <Col style={{ display: "flex", gap: 8 }}>
          <Upload {...uploadProps}>
            <Button type="primary" icon={<UploadOutlined />}>
              Cargar XLSX
            </Button>
          </Upload>
          <Button
            type="primary"
            onClick={() => {
              setAddNote({});
              setAddModalOpen(true);
            }}
          >
            Agregar Nota
          </Button>
        </Col>
      </Row>
      <Modal
        title="Selecciona las hojas a cargar"
        open={modalVisible}
        onOk={handleModalOk}
        onCancel={() => setModalVisible(false)}
        okButtonProps={{ disabled: selectedSheets.length === 0 || loading }}
        cancelButtonProps={{ disabled: loading }}
        maskClosable={!loading}
        closable={!loading}
      >
        {loading ? (
          <div style={{ textAlign: "center", padding: 32 }}>
            <span
              className="ant-spin ant-spin-spinning"
              style={{ fontSize: 24 }}
            >
              <svg
                viewBox="0 0 1024 1024"
                focusable="false"
                className="ant-spin-dot"
                data-icon="loading"
                width="1em"
                height="1em"
                fill="currentColor"
                aria-hidden="true"
              >
                <path d="M988 548H836c-17.7 0-32-14.3-32-32s14.3-32 32-32h152c17.7 0 32 14.3 32 32s-14.3 32-32 32zM220 516c0 17.7-14.3 32-32 32H36c-17.7 0-32-14.3-32-32s14.3-32 32-32h152c17.7 0 32 14.3 32 32zm292-292c-17.7 0-32-14.3-32-32V36c0-17.7 14.3-32 32-32s32 14.3 32 32v152c0 17.7-14.3 32-32 32zm0 576c17.7 0 32 14.3 32 32v152c0 17.7-14.3 32-32 32s-32-14.3-32-32V832c0-17.7 14.3-32 32-32zm282.6-434.6c12.5-12.5 32.8-12.5 45.3 0s12.5 32.8 0 45.3l-107.5 107.5c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3l107.5-107.5zm-565.2 565.2c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3l107.5-107.5c12.5-12.5 32.8-12.5 45.3 0s12.5 32.8 0 45.3L229.4 904.6zm0-722.6l107.5 107.5c12.5 12.5 12.5 32.8 0 45.3s-32.8 12.5-45.3 0L184.1 227.3c-12.5-12.5-12.5-32.8 0-45.3s32.8-12.5 45.3 0zm565.2 565.2l-107.5-107.5c-12.5-12.5-12.5-32.8 0-45.3s32.8-12.5 45.3 0l107.5 107.5c12.5 12.5 12.5 32.8 0 45.3s-32.8 12.5-45.3 0z"></path>
              </svg>
            </span>
            <div style={{ marginTop: 16 }}>Importando notas...</div>
          </div>
        ) : (
          <Checkbox.Group
            value={selectedSheets}
            onChange={handleSheetSelect}
            disabled={loading}
          >
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
        )}
      </Modal>
      <h1>Notas</h1>
      {isLoadingNotes ? (
        <div>Cargando notas...</div>
      ) : errorNotes ? (
        <div>Error al cargar notas</div>
      ) : (
        <NotesTable selectedDates={selectedDates} notes={notes || []} />
      )}
      <NoteEditModal
        open={addModalOpen}
        note={addNote}
        onSave={handleAddNote}
        onCancel={() => setAddModalOpen(false)}
      />
    </div>
  );
};

export default NotesPage;
