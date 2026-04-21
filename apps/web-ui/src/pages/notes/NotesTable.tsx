import React, { useRef, useState } from "react";
import dayjs from "dayjs";
import { NoteDto } from "@repo/shared/index";
import { SearchOutlined, EditOutlined } from "@ant-design/icons";
import type {
  InputRef,
  TableColumnsType,
  TableColumnType,
  TableProps,
} from "antd";
import { Button, Input, Space, Table, message } from "antd";
import type { FilterDropdownProps } from "antd/es/table/interface";
import Highlighter from "react-highlight-words";
import NoteEditModal from "./NoteEditModal";
import api from "../../services/Agent";

interface NotesTableProps {
  selectedDates: [dayjs.Dayjs | null, dayjs.Dayjs | null];
  notes: NoteDto[];
}

type DataIndex = keyof NoteDto;

const NotesTable: React.FC<NotesTableProps> = ({ notes }) => {
  const [searchText, setSearchText] = useState("");
  const [searchedColumn, setSearchedColumn] = useState("");
  const searchInput = useRef<InputRef>(null);

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

  const handleSave = async (note: NoteDto) => {
    try {
      await api.post("/notes/set-note", note);
      message.success("Nota actualizada correctamente");
      setEditModalOpen(false);
    } catch (err) {
      message.error("Error al actualizar la nota");
    }
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
      ...getColumnSearchProps("sentiment"),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Valor",
      dataIndex: "value",
      key: "value",
      ...getColumnSearchProps("value"),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Audiencia",
      dataIndex: "audience",
      key: "audience",
      ...getColumnSearchProps("audience"),
      ellipsis: true,
      width: "100px",
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
      title: "Editar",
      key: "editar",
      render: (_, record) => (
        <Button
          icon={<EditOutlined />}
          onClick={() => handleEdit(record)}
          type="default"
        />
      ),
      width: "80px",
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
    dataSource: notes,
    pagination: {
      current: pagination.current,
      pageSize: pagination.pageSize,
      pageSizeOptions: ["10", "20", "50", "100"],
      showSizeChanger: true,
    },
    onChange: handleTableChange,
  };

  return (
    <>
      <Table {...tableProps} />
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
