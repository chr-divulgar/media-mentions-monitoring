import React, { useEffect, useMemo, useRef, useState } from "react";
import dayjs from "dayjs";
import { NoteDto, NoteSentiment } from "@repo/shared/index";
import { SearchOutlined, EditOutlined, DeleteOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "react-query";
import type {
  InputRef,
  TableColumnsType,
  TableColumnType,
  TableProps,
} from "antd";
import { Button, Input, Popconfirm, Select, Space, Table, message } from "antd";
import type { FilterDropdownProps } from "antd/es/table/interface";
import type { TableRowSelection } from "antd/es/table/interface";
import Highlighter from "react-highlight-words";
import NoteEditModal from "./NoteEditModal";
import api from "../../services/Agent";

interface NotesTableProps {
  selectedDates: [dayjs.Dayjs | null, dayjs.Dayjs | null];
  notes: NoteDto[];
}

interface ClientTopicNode {
  name?: string;
  subtopics?: Array<{ name?: string }>;
}

interface ClientWithTopics {
  id?: string;
  name?: string;
  topics?: ClientTopicNode[];
}

type DataIndex = keyof NoteDto;

const NotesTable: React.FC<NotesTableProps> = ({ notes }) => {
  const [tableNotes, setTableNotes] = useState<NoteDto[]>(notes);
  const [searchText, setSearchText] = useState("");
  const [searchedColumn, setSearchedColumn] = useState("");
  const searchInput = useRef<InputRef>(null);
  const queryClient = useQueryClient();
  const [deletingId, setDeletingId] = useState<string>();
  const [updatingId, setUpdatingId] = useState<string>();
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([]);
  const [bulkSentiment, setBulkSentiment] = useState<NoteSentiment | undefined>();
  const [bulkUpdating, setBulkUpdating] = useState(false);
  const [retryingArchiveId, setRetryingArchiveId] = useState<string>();
  const [subtopicSearchByNote, setSubtopicSearchByNote] = useState<Record<string, string>>({});

  useEffect(() => {
    setTableNotes(notes);
  }, [notes]);

  const { data: clients = [] } = useQuery<ClientWithTopics[]>(
    ["clients"],
    async () => {
      const res = await api.get("/clients");
      return res.data as ClientWithTopics[];
    },
    { staleTime: 5 * 60 * 1000 },
  );

  const clientByName = useMemo(() => {
    return new Map(clients.map((client) => [client.name, client]));
  }, [clients]);

  const sentimentOptions = [
    { label: NoteSentiment.NEUTRO, value: NoteSentiment.NEUTRO },
    { label: NoteSentiment.POSITIVO, value: NoteSentiment.POSITIVO },
    { label: NoteSentiment.NEGATIVO, value: NoteSentiment.NEGATIVO },
  ];

  const fold = (value?: string): string =>
    (value ?? "").normalize("NFD").replace(/\p{M}/gu, "").trim().toLowerCase();

  // The note goes, and with it its file in the archive (the PDF or the clip); the alert it came from stays.
  const handleDelete = async (record: NoteDto) => {
    if (!record.id) return;
    setDeletingId(record.id);
    try {
      await api.delete(`/notes/${record.id}`);
      message.success("Nota eliminada");
      queryClient.invalidateQueries(["notes"]);
    } catch (error) {
      message.error(
        (error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? "No se pudo eliminar la nota",
      );
    } finally {
      setDeletingId(undefined);
    }
  };

  const handleSearch = (
    selectedKeys: string[],
    confirm: FilterDropdownProps["confirm"],
    dataIndex: DataIndex,
  ) => {
    confirm();
    setSearchText(selectedKeys[0]);
    setSearchedColumn(dataIndex);
  };

  const handleReset = (clearFilters: () => void) => {
    clearFilters();
    setSearchText("");
  };

  const getColumnSearchProps = (
    dataIndex: DataIndex,
  ): TableColumnType<NoteDto> => ({
    filterDropdown: ({
      setSelectedKeys,
      selectedKeys,
      confirm,
      clearFilters,
      close,
    }) => (
      <div style={{ padding: 8 }} onKeyDown={(e) => e.stopPropagation()}>
        <Input
          ref={searchInput}
          placeholder={`Buscar ${dataIndex}`}
          value={selectedKeys[0]}
          onChange={(e) =>
            setSelectedKeys(e.target.value ? [e.target.value] : [])
          }
          onPressEnter={() =>
            handleSearch(selectedKeys as string[], confirm, dataIndex)
          }
          style={{ marginBottom: 8, display: "block" }}
        />
        <Space>
          <Button
            type="primary"
            onClick={() =>
              handleSearch(selectedKeys as string[], confirm, dataIndex)
            }
            icon={<SearchOutlined />}
            size="small"
            style={{ width: 90 }}
          >
            Buscar
          </Button>
          <Button
            onClick={() => clearFilters && handleReset(clearFilters)}
            size="small"
            style={{ width: 90 }}
          >
            Reset
          </Button>
          <Button
            type="link"
            size="small"
            onClick={() => {
              close();
            }}
          >
            cerrar
          </Button>
        </Space>
      </div>
    ),
    filterIcon: (filtered: boolean) => (
      <SearchOutlined style={{ color: filtered ? "#1677ff" : undefined }} />
    ),
    onFilter: (value, record) => {
      if (!record) return false;
      const recordValue = record[dataIndex];
      if (recordValue === undefined || recordValue === null) return false;
      return recordValue
        .toString()
        .toLowerCase()
        .includes((value as string).toLowerCase());
    },
    onFilterDropdownOpenChange: (visible) => {
      if (visible) {
        setTimeout(() => searchInput.current?.select(), 100);
      }
    },
    render: (text) =>
      searchedColumn === dataIndex ? (
        <Highlighter
          highlightStyle={{ backgroundColor: "#ffc069", padding: 0 }}
          searchWords={[searchText]}
          autoEscape
          textToHighlight={text ? text.toString() : ""}
        />
      ) : (
        text
      ),
  });

  const [editModalOpen, setEditModalOpen] = useState(false);
  const [editingNote, setEditingNote] = useState<NoteDto | null>(null);

  const handleEdit = (note: NoteDto) => {
    setEditingNote(note);
    setEditModalOpen(true);
  };

  const updateSingleNote = async (note: NoteDto, patch: Partial<NoteDto>) => {
    if (!note.id) return;
    setUpdatingId(note.id);
    try {
      const payload = { ...note, ...patch };
      await api.post("/notes/set-note", payload);
      setTableNotes((prev) => prev.map((item) => (item.id === note.id ? { ...item, ...patch } : item)));
      queryClient.invalidateQueries(["notes"]);
    } catch {
      message.error("No se pudo actualizar la nota");
    } finally {
      setUpdatingId(undefined);
    }
  };

  const handleSave = async (note: NoteDto) => {
    try {
      await api.post("/notes/set-note", note);
      message.success("Nota actualizada correctamente");
      setTableNotes((prev) => prev.map((item) => (item.id === note.id ? { ...item, ...note } : item)));
      queryClient.invalidateQueries(["notes"]);
      setEditModalOpen(false);
    } catch (err) {
      message.error("Error al actualizar la nota");
    }
  };

  const getSubtopicOptions = (note: NoteDto) => {
    const client = clientByName.get(note.clientName);
    const topic = (client?.topics ?? []).find((candidate) => candidate.name === note.topic);
    const options = (topic?.subtopics ?? [])
      .filter((item) => !!item?.name)
      .map((item) => ({ label: item.name as string, value: item.name as string }));

    const currentSubtopic = (note.subtopic ?? "").trim();
    if (
      currentSubtopic.length > 0 &&
      !options.some((option) => fold(option.value) === fold(currentSubtopic))
    ) {
      options.unshift({ label: currentSubtopic, value: currentSubtopic });
    }

    return options;
  };

  const handleAddSubtopic = async (note: NoteDto, newName: string) => {
    const client = clientByName.get(note.clientName);
    if (!client?.id || !note.topic) {
      message.warning("Para crear subtema, la nota debe tener cliente y tema");
      return;
    }

    try {
      const response = await api.post(`/clients/${client.id}/subtopics`, {
        topic: note.topic,
        name: newName,
      });
      await queryClient.refetchQueries(["clients"]);
      await updateSingleNote(note, { subtopic: (response.data as { name: string }).name });
      message.success("Subtema agregado");
    } catch {
      message.error("No se pudo agregar el subtema");
    }
  };

  const handleBulkSentimentApply = async () => {
    if (!bulkSentiment || selectedRowKeys.length === 0) {
      return;
    }

    setBulkUpdating(true);
    try {
      const noteIds = selectedRowKeys.map(String);
      const res = await api.post("/notes/bulk-sentiment", {
        noteIds,
        sentiment: bulkSentiment,
      });
      const notFoundIds = (res.data?.notFoundIds ?? []) as string[];
      const notFoundSet = new Set(notFoundIds);
      setTableNotes((prev) =>
        prev.map((item) =>
          item.id && noteIds.includes(item.id) && !notFoundSet.has(item.id)
            ? { ...item, sentiment: bulkSentiment }
            : item,
        ),
      );
      setSelectedRowKeys([]);
      queryClient.invalidateQueries(["notes"]);
      if (notFoundIds.length > 0) {
        message.warning(`Se actualizaron ${res.data?.updatedCount ?? 0} notas; ${notFoundIds.length} no se encontraron`);
      } else {
        message.success(`Sentimiento actualizado en ${res.data?.updatedCount ?? 0} notas`);
      }
    } catch {
      message.error("No se pudo actualizar el sentimiento en lote");
    } finally {
      setBulkUpdating(false);
    }
  };

  const handleRetryArchive = async (note: NoteDto) => {
    if (!note.id) return;
    setRetryingArchiveId(note.id);
    try {
      const response = await api.post("/notes/retry-archive", { noteId: note.id });
      const updated = response.data?.note as NoteDto | undefined;
      if (updated?.id) {
        setTableNotes((prev) => prev.map((item) => (item.id === updated.id ? { ...item, ...updated } : item)));
      }
      queryClient.invalidateQueries(["notes"]);

      const finalStatus = updated?.archiveUploadStatus;
      if (finalStatus === "uploaded") {
        message.success("Archivo subido correctamente");
      } else if (finalStatus === "failed") {
        message.warning(updated?.archiveUploadError ?? "Falló nuevamente la subida del archivo");
      } else {
        message.info("Reintento enviado");
      }
    } catch {
      message.error("No se pudo reintentar la subida del archivo");
    } finally {
      setRetryingArchiveId(undefined);
    }
  };

  const renderArchiveStatus = (record: NoteDto) => {
    const status = record.archiveUploadStatus;
    if (status === "uploaded") return "Subido";
    if (status === "failed") return "Fallido";
    if (status === "pending") return "Pendiente";
    if (status === "not_required") return "No aplica";

    const media = (record.media ?? "").trim().toLowerCase();
    if (["radio", "tv", "television"].includes(media)) {
      return record.link ? "Subido" : "Pendiente";
    }

    return "No aplica";
  };

  const canRetryArchive = (record: NoteDto): boolean => {
    const media = (record.media ?? "").trim().toLowerCase();
    if (!["radio", "tv", "television"].includes(media)) return false;
    if (!record.id) return false;

    const status = record.archiveUploadStatus;
    if (status === "uploaded" || status === "not_required") return false;
    return true;
  };

  const columns: TableColumnsType<NoteDto> = [
    {
      title: "Fecha",
      dataIndex: "date",
      key: "date",
      ...getColumnSearchProps("date"),
      sorter: (a, b) => (a.date ?? "").localeCompare(b.date ?? ""),
      ellipsis: true,
      width: "110px",
    },
    {
      title: "No. nota",
      key: "index",
      render: (_, record) => record.index ?? record.audioLabel ?? "",
      width: "130px",
      ellipsis: true,
    },
    {
      title: "Emisora",
      dataIndex: "mediaName",
      key: "mediaName",
      ...getColumnSearchProps("mediaName"),
      ellipsis: true,
      width: "180px",
    },
    {
      title: "Medio",
      dataIndex: "media",
      key: "media",
      filters: [
        { text: "Prensa", value: "Prensa" },
        { text: "Internet", value: "Internet" },
        { text: "Televisión", value: "Televisión" },
        { text: "Radio", value: "Radio" },
      ],
      onFilter: (value, record) =>
        (record.media ?? "").includes(value as string),
      ellipsis: true,
      width: "120px",
    },
    {
      title: "Título",
      dataIndex: "title",
      key: "title",
      ...getColumnSearchProps("title"),
      ellipsis: true,
      width: "200px",
    },
    {
      title: "Resumen",
      dataIndex: "summary",
      key: "summary",
      ...getColumnSearchProps("summary"),
      ellipsis: true,
      width: "300px",
    },
    {
      title: "Sentimiento",
      dataIndex: "sentiment",
      key: "sentiment",
      render: (_, record) => (
        <Select
          size="small"
          style={{ width: "100%" }}
          options={sentimentOptions}
          value={record.sentiment as NoteSentiment | undefined}
          loading={updatingId === record.id}
          onChange={(value) => updateSingleNote(record, { sentiment: value })}
        />
      ),
      width: "140px",
    },
    {
      title: "Subtema",
      dataIndex: "subtopic",
      key: "subtopic",
      render: (_, record) => {
        const options = getSubtopicOptions(record);
        const typed = (subtopicSearchByNote[record.id ?? ""] ?? "").trim();
        const canAdd =
          typed.length > 0 &&
          !!record.topic &&
          !options.some((option) => fold(option.label) === fold(typed));

        return (
          <Select
            size="small"
            showSearch
            allowClear
            disabled={!record.topic}
            value={record.subtopic}
            options={options}
            filterOption={(input, option) =>
              fold(String(option?.label ?? "")).includes(fold(input))
            }
            onChange={(value) => updateSingleNote(record, { subtopic: value })}
            loading={updatingId === record.id}
            searchValue={subtopicSearchByNote[record.id ?? ""]}
            onSearch={(value) => {
              const key = record.id ?? "";
              if (!key) return;
              setSubtopicSearchByNote((prev) => ({ ...prev, [key]: value }));
            }}
            onBlur={() => {
              const key = record.id ?? "";
              if (!key) return;
              setSubtopicSearchByNote((prev) => ({ ...prev, [key]: "" }));
            }}
            popupRender={(menu) => (
              <>
                {menu}
                {canAdd && (
                  <Button
                    type="link"
                    style={{ width: "100%", textAlign: "left" }}
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => handleAddSubtopic(record, typed)}
                  >
                    Agregar «{typed}»
                  </Button>
                )}
              </>
            )}
          />
        );
      },
      width: "180px",
    },
    {
      title: "Link",
      dataIndex: "link",
      key: "link",
      render: (text) =>
        text ? (
          <a href={text} target="_blank" rel="noopener noreferrer">
            Ver
          </a>
        ) : (
          ""
        ),
      ellipsis: true,
      width: "80px",
    },
    {
      title: "Archivo",
      key: "archive",
      render: (_, record) => (
        <Space size={6}>
          <span>{renderArchiveStatus(record)}</span>
          {canRetryArchive(record) && (
            <Button
              size="small"
              onClick={() => handleRetryArchive(record)}
              loading={retryingArchiveId === record.id}
            >
              Reintentar
            </Button>
          )}
        </Space>
      ),
      width: "180px",
    },
    {
      title: "Acciones",
      key: "editar",
      render: (_, record) => (
        <Space size={4}>
          <Button
            icon={<EditOutlined />}
            onClick={() => handleEdit(record)}
            type="default"
            title="Editar"
          />
          <Popconfirm
            title="¿Eliminar la nota?"
            description="También se borra su archivo (PDF o clip) del servidor de archivo."
            okText="Eliminar"
            okButtonProps={{ danger: true }}
            cancelText="Cancelar"
            onConfirm={() => handleDelete(record)}
          >
            <Button icon={<DeleteOutlined />} danger loading={deletingId === record.id} title="Eliminar" />
          </Popconfirm>
        </Space>
      ),
      width: "100px",
      fixed: "right",
    },
  ];

  // Estado para paginación controlada
  const [pagination, setPagination] = useState({ current: 1, pageSize: 10 });

  const handleTableChange = (pag: any) => {
    setPagination({ current: pag.current, pageSize: pag.pageSize });
  };

  const tableProps: TableProps<NoteDto> = {
    size: "small",
    rowKey: "id",
    columns: columns,
    dataSource: tableNotes,
    pagination: {
      current: pagination.current,
      pageSize: pagination.pageSize,
      pageSizeOptions: ["10", "20", "50", "100"],
      showSizeChanger: true,
    },
    onChange: handleTableChange,
  };

  const rowSelection: TableRowSelection<NoteDto> = {
    selectedRowKeys,
    onChange: (keys: React.Key[]) => setSelectedRowKeys(keys),
    getCheckboxProps: (record: NoteDto) => ({ disabled: !record.id }),
  };

  return (
    <>
      <Space style={{ marginBottom: 12 }} wrap>
        <Select
          placeholder="Sentimiento masivo"
          value={bulkSentiment}
          onChange={(value) => setBulkSentiment(value)}
          options={sentimentOptions}
          style={{ minWidth: 180 }}
        />
        <Button
          type="primary"
          disabled={!bulkSentiment || selectedRowKeys.length === 0}
          loading={bulkUpdating}
          onClick={handleBulkSentimentApply}
        >
          Aplicar a seleccionadas ({selectedRowKeys.length})
        </Button>
      </Space>
      <Table {...tableProps} rowSelection={rowSelection} />
      <NoteEditModal
        open={editModalOpen}
        note={editingNote}
        onSave={handleSave}
        onCancel={() => setEditModalOpen(false)}
      />
    </>
  );
};

export default NotesTable;
