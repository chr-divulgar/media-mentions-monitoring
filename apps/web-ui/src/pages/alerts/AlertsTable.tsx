import React, { useRef, useState } from "react";
import dayjs from "dayjs";
import utc from "dayjs/plugin/utc";

dayjs.extend(utc);
import {
  AlertDto,
  AlertSlaSummaryDto,
  ALERT_SLA_TARGET_SECONDS,
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
import { Alert as AntAlert, Button, Input, Radio, Space, Table, Tag } from "antd";
import type { FilterDropdownProps } from "antd/es/table/interface";
import Highlighter from "react-highlight-words";
import AlertsModal from "./AlertsModal";
import InternetNoteModal from "./InternetNoteModal";
import { AlertRow, groupRepeatedAlerts, hideRepeatedWithinMinute } from "./alertGrouping";
import api from "../../services/Agent";
import { useQuery, UseQueryResult } from "react-query";
import { useAlert } from "./AlertsContext";

// worker alerts store a real UTC instant; show it fixed at Colombia time (UTC-5)
// regardless of the viewer's own browser timezone.
const getWorkerDate = (dateString: string | Date | dayjs.Dayjs) =>
  dayjs.utc(dateString).utcOffset(-300);

interface AlertsTableProps {
  selectedDates: DateRange | null;
  // The Nivel and SLA columns and the SLA summary (RQ-031) are built but hidden for now; pass
  // `showSla` to bring them back.
  showSla?: boolean;
}

// Media filter of the table. Television alerts are stored as "tv" or "television".
type MediaFilter = "all" | "radio" | "television" | "internet";
const MEDIA_FILTERS: { value: MediaFilter; label: string }[] = [
  { value: "all", label: "Todas" },
  { value: "radio", label: "radio" },
  { value: "television", label: "television" },
  { value: "internet", label: "internet" },
];
const matchesMedia = (alertMedia: string | undefined, filter: MediaFilter): boolean => {
  const media = (alertMedia ?? "").toLowerCase();
  if (filter === "all") return true;
  return filter === "television" ? media === "tv" || media === "television" : media === filter;
};
type DataIndex = keyof AlertDto;

const AlertsTable: React.FC<AlertsTableProps> = ({ selectedDates, showSla = false }) => {
  const [mediaFilter, setMediaFilter] = useState<MediaFilter>("all");
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
  // Internet alerts have no audio/video to cut: they open the PDF-based editor instead.
  const [internetModalOpen, setInternetModalOpen] = useState(false);
  const { setSelectedAlert } = useAlert();

  const showModal = (alert: AlertDto) => {
    setModalKey((prevKey) => prevKey + 1);
    setSelectedAlert(alert);
    if (alert.media === "internet") {
      setInternetModalOpen(true);
    } else {
      setModalOpen(true);
    }
  };

  const hideModal = () => {
    setModalOpen(false);
    setInternetModalOpen(false);
  };

  //#endregion

  //#region Alerts
  const {
    data: alerts,
    isLoading: isLoadingAlerts,
    error: errorAlerts,
  }: UseQueryResult<AlertDto[]> = useQuery<AlertDto[]>({
    queryKey: ["alerts", selectedDates],
    queryFn: async () =>
      await api
        .post(`/alerts`, {
          startDate: selectedDates?.[0]?.format(dateFormat),
          endDate: selectedDates?.[1]?.format(dateFormat),
          // No type filter: every alert the worker stored is listed, ads and repeats included.
          source: "worker",
        })
        .then((res) => res.data),
  });

  // SLA compliance (RQ-031). Declared before the early returns below so the hook order never
  // changes between renders.
  const { data: slaSummary } = useQuery<AlertSlaSummaryDto>({
    queryKey: ["alertsSla", selectedDates?.[0]?.format(dateFormat), selectedDates?.[1]?.format(dateFormat)],
    queryFn: () =>
      api
        .post(`/alerts/sla`, {
          startDate: selectedDates?.[0]?.format(dateFormat),
          endDate: selectedDates?.[1]?.format(dateFormat),
        })
        .then((res) => res.data),
    enabled: showSla && !!selectedDates?.[0] && !!selectedDates?.[1],
  });

  if (isLoadingAlerts) return <div>Loading...</div>;
  if (errorAlerts) return <div>Error loading Alerts</div>;
  //#endregion

  const columns: TableColumnsType<AlertRow> = [
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
      render: (text) => getWorkerDate(text).format("DD/MM/YY"),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Hora",
      dataIndex: "startTime",
      key: "startTime",
      //sorter: (a, b) => a.startTime.getTime() - b.startTime.getTime(),
      render: (text) => getWorkerDate(text).format("HH:mm:ss"),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Tipo",
      dataIndex: "type",
      key: "type",
      filters: [
        { text: "Nueva", value: "Nueva" },
        { text: "Anuncio", value: "Anuncio" },
        { text: "RepetidaOtraPlataforma", value: "RepetidaOtraPlataforma" },
      ],
      render: (type?: string, record?: AlertRow) =>
        record?.children?.length ? `${type ?? ""} (+${record.children.length})` : type,
      onFilter: (value, record) =>
        (record.type ?? "").includes(value as string),
      sorter: (a, b) => (a.type ?? "").length - (b.type ?? "").length,
      ellipsis: true,
      width: "150px",
    },
    ...(showSla
      ? [
          {
            title: "Nivel",
            dataIndex: "level",
            key: "level",
            width: "70px",
            render: (level?: string) => {
              const label = level ?? "N1";
              const color = { N2: "gold", N3: "orange", N4: "red" }[label];
              return <Tag color={color}>{label}</Tag>;
            },
          },
          {
            title: "SLA",
            dataIndex: "slaSeconds",
            key: "slaSeconds",
            width: "80px",
            render: (seconds?: number) =>
              seconds === undefined || seconds === null ? (
                "—"
              ) : (
                <span style={{ color: seconds > ALERT_SLA_TARGET_SECONDS ? "#cf1322" : undefined }}>
                  {seconds < 60 ? `${Math.round(seconds)} s` : `${(seconds / 60).toFixed(1)} min`}
                </span>
              ),
          },
        ]
      : []),
    {
      title: "Palabras",
      dataIndex: "words",
      key: "words",
      ...getColumnSearchProps("text"),
      render: (words) => (words ?? []).join(", "),
      ellipsis: true,
      width: "100px",
    },
    {
      title: "Texto",
      dataIndex: "text",
      key: "text",
      ...getColumnSearchProps("text"),
      render: (text, record) => {
        if (record.media === "internet") {
          // A web mention: the headline links straight to the article, and the stored text is
          // already just the first paragraph — no audio-oriented keyword highlighting to apply.
          return (
            <div style={{ whiteSpace: "normal" }}>
              {record.url ? (
                <a href={record.url} target="_blank" rel="noreferrer">
                  <strong>{record.title}</strong>
                </a>
              ) : (
                <strong>{record.title}</strong>
              )}
              <div>{text}</div>
            </div>
          );
        }

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

  const tableProps: TableProps<AlertRow> = {
    size: "small",
    rowKey: "id",
    columns: columns,
    // Repeats fold under the new alert they repeat; the table's tree rows give the "+" to expand them.
    // Groups never cross media, so filtering by media first leaves every group whole. Same-platform
    // repeats within a minute are hidden once the groups are built (see hideRepeatedWithinMinute).
    dataSource: hideRepeatedWithinMinute(
      groupRepeatedAlerts((alerts ?? []).filter((alert) => matchesMedia(alert.media, mediaFilter))),
    ),
    pagination: { pageSize: 10 },
  };

  return (
    <>
      <Radio.Group
        value={mediaFilter}
        onChange={(e) => setMediaFilter(e.target.value)}
        style={{ marginBottom: 8 }}
      >
        {MEDIA_FILTERS.map((option) => (
          <Radio.Button key={option.value} value={option.value}>
            {option.label}
          </Radio.Button>
        ))}
      </Radio.Group>
      {showSla && slaSummary && slaSummary.measured > 0 && (
        <AntAlert
          style={{ marginBottom: 8 }}
          showIcon
          type={slaSummary.withinTarget === slaSummary.measured ? "success" : "warning"}
          message={`SLA ≤ ${Math.round(slaSummary.targetSeconds / 60)} min: ${slaSummary.withinTarget} de ${slaSummary.measured} alertas entregadas a tiempo (mediana ${Math.round(slaSummary.p50Seconds ?? 0)} s, p95 ${Math.round(slaSummary.p95Seconds ?? 0)} s)`}
        />
      )}
      <Table {...tableProps} />
      <AlertsModal
        key={modalKey}
        visible={modalOpen}
        onClose={hideModal}
      ></AlertsModal>
      <InternetNoteModal
        key={`internet-${modalKey}`}
        visible={internetModalOpen}
        onClose={hideModal}
      />
    </>
  );
};

export default AlertsTable;
