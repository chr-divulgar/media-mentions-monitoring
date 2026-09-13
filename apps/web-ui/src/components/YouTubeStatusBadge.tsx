import React, { useEffect, useState } from "react";
import { Tag, Tooltip, Spin } from "antd";
import {
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  CloseCircleOutlined,
} from "@ant-design/icons";
import api from "../services/Agent";
import type { YouTubeStatusDto } from "@repo/shared";

export interface YouTubeStatusBadgeProps {
  /** Refresh interval in milliseconds (default: 30000) */
  refreshInterval?: number;
}

/**
 * YouTubeStatusBadge - Display YouTube authentication status in header
 * Shows: healthy (green), degraded (orange), or unhealthy (red)
 * Auto-refreshes every 30 seconds by default
 */
export const YouTubeStatusBadge: React.FC<YouTubeStatusBadgeProps> = ({
  refreshInterval = 30000,
}) => {
  const [status, setStatus] = useState<YouTubeStatusDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetch status
  const fetchStatus = async () => {
    try {
      setLoading(true);
      setError(null);
      const response = await api.get("/settings/youtube/status");
      setStatus(response.data as YouTubeStatusDto);
    } catch (err: any) {
      setError(err.message || "Failed to fetch YouTube status");
      setStatus(null);
    } finally {
      setLoading(false);
    }
  };

  // Initial fetch and setup interval
  useEffect(() => {
    fetchStatus();
    const interval = setInterval(fetchStatus, refreshInterval);
    return () => clearInterval(interval);
  }, [refreshInterval]);

  // Get badge configuration based on status
  const getBadgeConfig = () => {
    if (loading) {
      return {
        icon: <Spin size="small" style={{ marginRight: "4px" }} />,
        color: "processing",
        label: "Checking...",
      };
    }

    if (error || !status) {
      return {
        icon: <CloseCircleOutlined />,
        color: "error",
        label: "Status Unknown",
      };
    }

    if (status.status === "healthy") {
      return {
        icon: <CheckCircleOutlined />,
        color: "success",
        label: "YouTube OK",
      };
    }

    if (status.status === "degraded") {
      return {
        icon: <ExclamationCircleOutlined />,
        color: "warning",
        label: "YouTube Degraded",
      };
    }

    // unhealthy
    return {
      icon: <CloseCircleOutlined />,
      color: "error",
      label: "YouTube Needs Auth",
    };
  };

  const config = getBadgeConfig();

  // Build tooltip content
  let tooltipContent = config.label;
  if (status) {
    const details = [
      `Cookies: ${status.cookiesFileExists ? "✓ Exists" : "✗ Missing"}`,
      `Valid: ${status.cookiesValid ? "✓ Yes" : "✗ No"}`,
    ];

    if (status.cookieCount) {
      details.push(`Count: ${status.cookieCount}`);
    }

    if (status.authAlertActive) {
      details.push("⚠️ Alert: Auth required");
    }

    if (status.excludedYouTubeSources && status.excludedYouTubeSources.length > 0) {
      details.push(`Excluded: ${status.excludedYouTubeSources.length} sources`);
    }

    tooltipContent = (
      <div>
        <div style={{ marginBottom: "8px", fontWeight: "bold" }}>{config.label}</div>
        <div style={{ fontSize: "12px" }}>
          {details.map((detail, idx) => (
            <div key={idx}>{detail}</div>
          ))}
        </div>
      </div>
    ) as any;
  }

  return (
    <Tooltip title={tooltipContent}>
      <Tag
        icon={config.icon}
        color={config.color}
        style={{ marginRight: 0 }}
      >
        {config.label}
      </Tag>
    </Tooltip>
  );
};

export default YouTubeStatusBadge;
