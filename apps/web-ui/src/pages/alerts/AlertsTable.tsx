import React, { useRef, useState } from "react";
import dayjs from "dayjs";
import utc from "dayjs/plugin/utc";

dayjs.extend(utc);
import {
  AlertDto,
  dateFormat,
  DateRange,
  transformText,
} from "@repo/shared/index";
import { SearchOutlined, EditOutlined } from "@ant-design/icons";
import type {
  InputRef,
  TableColumnsType,
  TableColumnType,
  TableProps,
} from "antd";
import { Button, Input, Space, Table } from "antd";
import type { FilterDropdownProps } from "antd/es/table/interface";
import Highlighter from "react-highlight-words";
import AlertsModal from "./AlertsModal";
import api from "../../services/Agent";
import { useQuery, UseQueryResult } from "react-query";
import { useAlert } from "./AlertsContext";

// Función para formatear la fecha asumiendo que es UTC
const getDateOffset = (dateString: string | Date | dayjs.Dayjs) => {
  const date = dayjs(dateString);
  const dateWithFixedOffset = date.add(5, "hour");
  return dateWithFixedOffset;
};

// worker alerts store a real UTC instant; show it fixed at Colombia time (UTC-5)
// regardless of the viewer's own browser timezone.
const getWorkerDate = (dateString: string | Date | dayjs.Dayjs) =>
  dayjs.utc(dateString).utcOffset(-300);

interface AlertsTableProps {
  selectedDates: DateRange | null;
  source?: "legacy" | "worker";
  title?: string;
}
type DataIndex = keyof AlertDto;

const AlertsTable: React.FC<AlertsTableProps> = ({
  selectedDates,
  source,
  title,
}) => {
  //#region Table Handlers
  const [searchText, setSearchText] = useState("");
  const [searchedColumn, setSearchedColumn] = useState("");

  const searchInput = useRef<InputRef>(null);

  const handleSearch = (
    selectedKeys: string[],
    confirm: FilterDropdownProps["confirm"],
    dataIndex: DataIndex
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
    dataIndex: DataIndex
  ): TableColumnType<AlertDto> => ({
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
          placeholder={`Search ${dataIndex}`}
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
            Search
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
              confirm({ closeDropdown: false });
              setSearchText((selectedKeys as string[])[0]);
              setSearchedColumn(dataIndex);
            }}
          >
            Filter
          </Button>
          <Button
            type="link"
            size="small"
            onClick={() => {
              close();
            }}
          >
            close
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

  //#endregion
  //#region Modal Handlers

  const [modalOpen, setModalOpen] = useState(false);
  const [modalKey, setModalKey] = useState(0);
  const { setSelectedAlert } = useAlert();

  const showModal = (alert: AlertDto) => {
    setModalKey((prevKey) => prevKey + 1);
    setSelectedAlert(alert);
    setModalOpen(true);
  };

  const hideModal = () => {
    setModalOpen(false);
  };

  //#endregion

  //#region Alerts
  const {
    data: alerts,
    isLoading: isLoadingAlerts,
    error: errorAlerts,
  }: UseQueryResult<AlertDto[]> = useQuery<AlertDto[]>({
    queryKey: ["alerts", selectedDates, source],
    queryFn: async () =>
      await api
        .post(`/alerts`, {
          startDate: selectedDates?.[0]?.format(dateFormat),
          endDate: selectedDates?.[1]?.format(dateFormat),
          type: ["Nueva", "RepetidaOtraPlataforma"],
          ...(source && { source }),
        })
        .then((res) => res.data),
  });

  if (isLoadingAlerts) return <div>Loading...</div>;
  if (errorAlerts) return <div>Error loading Alerts</div>;
  //#endregion

  const columns: TableColumnsType<AlertDto> = [
    {
      title: "Cliente",
      dataIndex: "clientName",
      key: "clientName",
      filters: [
        { text: "Ecopetrol", value: "Ecopetrol" },
        { text: "Anthony", value: "Anthony" },
      ],
      onFilter: (value, record) =>
        (record.clientName ?? "").includes(value as string),
      sorter: (a, b) =>
        (a.clientName ?? "").length - (b.clientName ?? "").length,
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Plataforma",
      dataIndex: "platform",
      key: "platform",
      ...getColumnSearchProps("platform"),
      sorter: (a, b) => (a.platform ?? "").length - (b.platform ?? "").length,
      ellipsis: true,
      width: "150px",
    },
    {
      title: "Fecha",
      dataIndex: "startTime",
      key: "startTime",
      //sorter: (a, b) => a.startTime.getTime() - b.startTime.getTime(),
      render: (text) =>
        (source === "worker" ? getWorkerDate(text) : getDateOffset(text)).format(
          "DD/MM/YY",
        ),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Hora",
      dataIndex: "startTime",
      key: "startTime",
      //sorter: (a, b) => a.startTime.getTime() - b.startTime.getTime(),
      render: (text) =>
        (source === "worker" ? getWorkerDate(text) : getDateOffset(text)).format(
          "HH:mm:ss",
        ),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Tipo",
      dataIndex: "type",
      key: "type",
      filters: [
        { text: "Nueva", value: "Nueva" },
        { text: "RepetidaOtraPlataforma", value: "RepetidaOtraPlataforma" },
      ],
      onFilter: (value, record) =>
        (record.type ?? "").includes(value as string),
      sorter: (a, b) => (a.type ?? "").length - (b.type ?? "").length,
      ellipsis: true,
      width: "150px",
    },
    {
      title: "Palabras",
      dataIndex: "words",
      key: "words",
      ...getColumnSearchProps("text"),
      render: (words) => words.join(", "),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Texto",
      dataIndex: "text",
      key: "text",
      ...getColumnSearchProps("text"),
      render: (text, record) => {
        // Apply the transformation
        const finalTransformedText = transformText(text, record.words ?? []);

        // Return the transformed text as HTML
        return (
          <div
            style={{ whiteSpace: "normal" }}
            dangerouslySetInnerHTML={{ __html: finalTransformedText }}
          />
        );
      },
    },
    {
      title: "Editar",
      dataIndex: "filePath",
      key: "filePath",
      ellipsis: true,
      render: (_, record) => (
        <Button
          icon={<EditOutlined />}
          onClick={() => showModal(record)}
        ></Button>
      ),
      width: "60px",
    },
  ];

  const tableProps: TableProps<AlertDto> = {
    size: "small",
    rowKey: "id",
    columns: columns,
    dataSource: alerts,
    pagination: { pageSize: 10 },
  };

  return (
    <>
      {title && <h3>{title}</h3>}
      <Table {...tableProps} />
      <AlertsModal
        key={modalKey}
        visible={modalOpen}
        onClose={hideModal}
      ></AlertsModal>
    </>
  );
};

export default AlertsTable;
