import React, { useState, useRef, useEffect } from "react";
import {
  DatePicker,
  Button,
  Row,
  Col,
  Upload,
  Modal,
  Checkbox,
  Select,
  message,
} from "antd";
import type { AxiosError } from "axios";
import type { ImportNoteRowDto, ImportNotesResultDto } from "@repo/shared";
import { FileWordOutlined, UploadOutlined } from "@ant-design/icons";
import * as XLSX from "xlsx";
import dayjs from "dayjs";
import isoWeek from "dayjs/plugin/isoWeek";
dayjs.extend(isoWeek);
import api from "../../services/Agent";
import { useQuery } from "react-query";
import DailyReportModal from "./DailyReportModal";
import NotesTable from "./NotesTable";
import NoteEditModal from "./NoteEditModal";
import {
  excelSerialToIsoDate,
  mapHeaders,
  missingRequired,
  type ImportField,
} from "./excelImport";

// What the API said went wrong: its own message (400) or error (500), not a generic text.
const apiErrorMessage = (error: unknown, fallback: string): string => {
  const data = (error as AxiosError<{ message?: unknown; error?: unknown }>)?.response?.data;
  if (typeof data?.message === "string") return data.message;
  if (typeof data?.error === "string") return data.error;
  return fallback;
};

// The import's result, shown as it is: what came in, what already existed and each row that could not.
const showImportResult = (result: ImportNotesResultDto) => {
  const lines = [
    `Importadas: ${result.inserted}`,
    `Ya existían (se conservan las del portal): ${result.duplicates}`,
    `Con error: ${result.errors.length}`,
  ];
  const content = (
    <div>
      {lines.map((line) => (
        <div key={line}>{line}</div>
      ))}
      {result.errors.length > 0 && (
        <div style={{ maxHeight: 280, overflowY: "auto", marginTop: 12 }}>
          {result.errors.slice(0, 200).map((error, index) => (
            <div key={index} style={{ fontSize: 12 }}>
              {error.sheet ? `${error.sheet}, fila ${error.row}: ` : ""}
              {error.reason}
            </div>
          ))}
          {result.errors.length > 200 && (
            <div style={{ fontSize: 12 }}>… y {result.errors.length - 200} más</div>
          )}
        </div>
      )}
    </div>
  );
  if (result.inserted === 0 && result.errors.length > 0) {
    Modal.error({ title: "No se importó ninguna nota", content, width: 560 });
  } else if (result.errors.length > 0) {
    Modal.warning({ title: "Importación con errores", content, width: 560 });
  } else {
    Modal.success({ title: "Notas importadas", content, width: 560 });
  }
};

const NotesPage: React.FC = () => {
  // Por defecto, seleccionar la semana del último dato (maxDate)
  const [selectedDates, setSelectedDates] = useState<any>(null);
  const [minDate, setMinDate] = useState<string | null>(null);
  const [maxDate, setMaxDate] = useState<string | null>(null);

  // Estado para modal de nueva nota
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [reportOpen, setReportOpen] = useState(false);
  const [addNote, setAddNote] = useState<any>(null);

  // Guardar nueva nota
  const handleAddNote = async (note: any) => {
    try {
      const { data } = await api.post<ImportNotesResultDto>("/notes/import-excel", {
        clientName: note.clientName,
        notes: [note],
      });
      if (data.inserted === 0) {
        message.error(
          data.errors[0]?.reason ?? "Esa nota ya existe (misma fecha, medio y titular)",
        );
        return;
      }
      message.success("Nota agregada correctamente");
      setAddModalOpen(false);
      refetch();
    } catch (err) {
      message.error(apiErrorMessage(err, "Error al agregar la nota"));
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
  // Each sheet of the file and the required columns it lacks (empty: it can be imported).
  const [sheets, setSheets] = useState<{ name: string; missing: string[] }[]>([]);
  const [selectedSheets, setSelectedSheets] = useState<string[]>([]);
  const [importClient, setImportClient] = useState<string | undefined>();
  const { data: clients = [] } = useQuery<{ name: string }[]>(
    ["clients"],
    async () => (await api.get("/clients")).data,
    { staleTime: 5 * 60 * 1000 },
  );
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
      // Only the sheets with the note columns can be imported; the others are summaries or pivot tables.
      const found = workbook.SheetNames.map((name) => {
        const [header = []] = XLSX.utils.sheet_to_json<unknown[]>(workbook.Sheets[name], {
          header: 1,
          defval: "",
        });
        return { name, missing: missingRequired(mapHeaders(header)) };
      });
      setSheets(found);
      setSelectedSheets(found.filter((sheet) => sheet.missing.length === 0).map((sheet) => sheet.name));
      setImportClient((current) => current ?? clients[0]?.name);
      setModalVisible(true);
    };
    reader.readAsArrayBuffer(file);
    return false; // Prevenir carga automática
  };

  const handleSheetSelect = (checkedValues: any) => {
    setSelectedSheets(checkedValues);
  };

  const normalizeWithSingleSeparator = (
    value: string,
    separator: "," | ".",
  ): string => {
    const parts = value.split(separator);
    if (parts.length === 2 && parts[1].length <= 2) {
      return value.replace(separator, ".");
    }
    return value.split(separator).join("");
  };

  const normalizeNumericText = (text: string): string => {
    const compact = text.replace(/\s+/g, "");
    const hasComma = compact.includes(",");
    const hasDot = compact.includes(".");

    if (hasComma && hasDot) {
      const lastComma = compact.lastIndexOf(",");
      const lastDot = compact.lastIndexOf(".");
      const decimalSep = lastComma > lastDot ? "," : ".";
      const thousandSep = decimalSep === "," ? "." : ",";
      return compact.split(thousandSep).join("").replace(decimalSep, ".");
    }

    if (hasComma) {
      return normalizeWithSingleSeparator(compact, ",");
    }

    if (hasDot) {
      return normalizeWithSingleSeparator(compact, ".");
    }

    return compact;
  };

  const toTruncatedInteger = (
    input: string | number | null | undefined,
  ): string | number | null | undefined => {
    if (input === null || input === undefined || input === "") return input;

    if (typeof input === "number") {
      return Math.trunc(input);
    }

    const text = input.trim();
    if (!text) return text;

    const normalized = normalizeNumericText(text);

    const numeric = Number(normalized);
    if (!Number.isFinite(numeric)) return input;
    return Math.trunc(numeric);
  };

  const handleModalOk = async () => {
    if (!workbookRef.current) return;
    if (!importClient) {
      message.error("Elige el cliente de las notas");
      return;
    }
    setLoading(true);
    const notes: ImportNoteRowDto[] = [];

    for (const sheetName of selectedSheets) {
      const ws = workbookRef.current.Sheets[sheetName];
      const range = XLSX.utils.decode_range(ws["!ref"] ?? "A1");
      const rows = XLSX.utils.sheet_to_json<unknown[]>(ws, { header: 1, defval: "", blankrows: true });
      const columns = mapHeaders(rows[0] ?? []);

      rows.slice(1).forEach((cells, index) => {
        if (cells.every((cell) => String(cell ?? "").trim() === "")) return;
        const sheetRow = range.s.r + index + 1; // zero-based, as the sheet counts it
        const note: Record<string, unknown> = { sheet: sheetName, row: sheetRow + 1 };

        for (const [field, column] of Object.entries(columns) as [ImportField, number][]) {
          let value = cells[column];
          if (field === "date") {
            const text = String(value ?? "").trim();
            value =
              typeof value === "number"
                ? excelSerialToIsoDate(value)
                : text && dayjs(text).isValid()
                  ? dayjs(text).format("YYYY-MM-DD")
                  : text; // left as it is: the API says which row has a date it cannot read
          } else if (field === "link") {
            // The cell shows the headline; the address is its hyperlink.
            const cell = ws[XLSX.utils.encode_cell({ r: sheetRow, c: range.s.c + column })];
            value = cell?.l?.Target ?? value;
          } else if (field === "rate" || field === "audience") {
            value = toTruncatedInteger(value as string | number);
          } else if (typeof value === "string") {
            value = value.trim();
          }
          note[field] = value;
        }
        notes.push(note as ImportNoteRowDto);
      });
    }

    try {
      const { data } = await api.post<ImportNotesResultDto>("/notes/import-excel", {
        clientName: importClient,
        notes,
      });
      setModalVisible(false);
      showImportResult(data);
      refetch();
    } catch (err) {
      message.error(apiErrorMessage(err, "No se pudieron cargar las notas"));
    } finally {
      setLoading(false);
    }
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
          <Button icon={<FileWordOutlined />} onClick={() => setReportOpen(true)}>
            Informe diario
          </Button>
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
      <DailyReportModal open={reportOpen} onClose={() => setReportOpen(false)} defaultRange={selectedDates} />
      <Modal
        title="Cargar notas desde Excel"
        open={modalVisible}
        onOk={handleModalOk}
        onCancel={() => setModalVisible(false)}
        okButtonProps={{ disabled: selectedSheets.length === 0 || !importClient || loading }}
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
          <>
            <div style={{ marginBottom: 12 }}>
              <span style={{ marginRight: 8 }}>Cliente:</span>
              <Select
                value={importClient}
                onChange={setImportClient}
                options={clients.map((client) => ({ value: client.name, label: client.name }))}
                style={{ minWidth: 220 }}
              />
            </div>
            <div style={{ marginBottom: 8 }}>Hojas a cargar:</div>
            <Checkbox.Group
              value={selectedSheets}
              onChange={handleSheetSelect}
              disabled={loading}
            >
              <Row gutter={[16, 8]}>
                {sheets.map(({ name, missing }) => (
                  <Col span={8} key={name} style={{ marginBottom: 8 }}>
                    <Checkbox
                      value={name}
                      disabled={missing.length > 0}
                      title={missing.length > 0 ? `No son notas: faltan las columnas ${missing.join(", ")}` : name}
                    >
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
                      >
                        {name}
                      </span>
                    </Checkbox>
                  </Col>
                ))}
              </Row>
            </Checkbox.Group>
            {sheets.some((sheet) => sheet.missing.length > 0) && (
              <div style={{ fontSize: 12, color: "#888", marginTop: 8 }}>
                Las hojas deshabilitadas no tienen las columnas de una nota (Fecha, Tipo de medio, Medio, Titular).
                Pasa el cursor sobre ellas para ver cuáles faltan.
              </div>
            )}
          </>
        )}
      </Modal>
      <h2>
        Notas ({notes ? notes.length : 0})
        {notes && notes.length > 0 && (
          <span
            style={{ fontWeight: "normal", fontSize: "1rem", marginLeft: 16 }}
          >
            {(
              Array.from(
                notes.reduce((acc: any, n: any) => {
                  const key = n.media || "Sin medio";
                  acc.set(key, (acc.get(key) || 0) + 1);
                  return acc;
                }, new Map()),
              ) as [any, any][]
            ).map(([media, count]) => (
              <span key={media} style={{ marginLeft: 8 }}>
                {media} ({count})
              </span>
            ))}
          </span>
        )}
      </h2>
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
