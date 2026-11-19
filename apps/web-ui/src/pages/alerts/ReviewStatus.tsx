import React from "react";
import dayjs from "dayjs";
import utc from "dayjs/plugin/utc";
import { InfoCircleOutlined } from "@ant-design/icons";
import { Button, Popover, Table, Tooltip } from "antd";
import { ALERT_REVIEW_STATUSES, AlertDto, AlertReviewStatus } from "@repo/shared/index";

dayjs.extend(utc);

// An alert the portal knows nothing about yet counts as not looked at.
const statusOf = (value?: AlertReviewStatus) =>
  ALERT_REVIEW_STATUSES.find((status) => status.value === value) ?? ALERT_REVIEW_STATUSES[0];

/** Where the status sits in the order of progress, for sorting the column. */
export const reviewStatusRank = (alert: AlertDto): number =>
  ALERT_REVIEW_STATUSES.indexOf(statusOf(alert.reviewStatus));

// The color is never the only signal: it carries its words for a screen reader and for the tooltip.
const Dot: React.FC<{ color: string; label: string }> = ({ color, label }) => (
  <span
    role="img"
    aria-label={label}
    style={{
      display: "inline-block",
      width: 14,
      height: 14,
      borderRadius: "50%",
      background: color,
      verticalAlign: "middle",
    }}
  />
);

/** The colored dot of an alert's status, with the status (and when it was first opened) on hover. */
export const ReviewStatusDot: React.FC<{ alert: AlertDto }> = ({ alert }) => {
  const status = statusOf(alert.reviewStatus);
  // The portal shows Colombia time (UTC-5) whatever the viewer's own time zone is.
  const opened = alert.viewedAt
    ? ` · abierta ${dayjs.utc(alert.viewedAt).utcOffset(-300).format("DD/MM HH:mm")}`
    : "";
  const text = `${status.label}${opened}`;
  return (
    <Tooltip title={text}>
      <Dot color={status.color} label={text} />
    </Tooltip>
  );
};

/** The table of conventions: what each color of the status column means. */
export const ReviewStatusLegend: React.FC = () => (
  <Popover
    trigger="click"
    placement="bottomRight"
    title="Convención de estados"
    content={
      <Table
        size="small"
        pagination={false}
        rowKey="value"
        dataSource={[...ALERT_REVIEW_STATUSES]}
        columns={[
          {
            title: "Color",
            dataIndex: "color",
            width: 60,
            align: "center",
            render: (color: string, status) => <Dot color={color} label={status.label} />,
          },
          { title: "Estado", dataIndex: "label" },
          { title: "Qué significa", dataIndex: "description" },
        ]}
      />
    }
  >
    <Button size="small" icon={<InfoCircleOutlined />}>
      Convención
    </Button>
  </Popover>
);
