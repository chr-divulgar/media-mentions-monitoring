import React, { useState } from "react";
import {
  Card,
  Button,
  Typography,
  Space,
  message,
  Spin,
  Row,
  Col,
  Divider,
  Alert,
  Statistic,
  Badge,
  List,
} from "antd";
import {
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  CloseOutlined,
  CheckOutlined,
  RobotOutlined,
} from "@ant-design/icons";
import { useQuery, useMutation } from "react-query";
import dayjs from "dayjs";
import api from "../../services/Agent";
import type {
  YouTubeStatusDto,
  ExtractCookiesResponseDto,
  ExtractCookiesDto,
  StartYouTubeLoginSessionResponseDto,
} from "@repo/shared";

const { Title, Text, Paragraph } = Typography;

export const YouTubeSettings: React.FC = () => {
  const [loginSessionId, setLoginSessionId] = useState("");
  const [hasActiveLoginSession, setHasActiveLoginSession] = useState(false);
  const [sessionExpiresAt, setSessionExpiresAt] = useState<string | null>(null);

  // Fetch YouTube status
  const { data: status, isLoading: statusLoading, refetch: refetchStatus } = useQuery(
    "youtubeStatus",
    async () => {
      const response = await api.get("/settings/youtube/status");
      return response.data as YouTubeStatusDto;
    },
    {
      refetchInterval: 30000, // Auto-refresh every 30 seconds
      staleTime: 10000,
    }
  );

  const startLoginSessionMutation = useMutation(
    async () => {
      const response = await api.post("/settings/youtube/login-session/start");
      return response.data as StartYouTubeLoginSessionResponseDto;
    },
    {
      onSuccess: (data) => {
        if (!data.success) {
          message.error(data.message || "Could not start login session");
          return;
        }

        setLoginSessionId(data.sessionId || "");
        setHasActiveLoginSession(true);
        setSessionExpiresAt(data.expiresAt || null);
        message.success("Login window opened. Complete sign-in there, then click Obtain Cookies.");
      },
      onError: (error: any) => {
        message.error(error.response?.data?.error || "Failed to start login session");
      },
    }
  );

  const obtainCookiesMutation = useMutation(
    async (request: ExtractCookiesDto) => {
      const response = await api.post("/settings/youtube/login-session/obtain-cookies", request);
      return response.data as ExtractCookiesResponseDto;
    },
    {
      onSuccess: (data) => {
        if (data.success) {
          message.success(data.message);
          setLoginSessionId("");
          setHasActiveLoginSession(false);
          setSessionExpiresAt(null);
          refetchStatus();
          return;
        }

        // If extraction fails, force a clean restart flow for the next attempt.
        setLoginSessionId("");
        setHasActiveLoginSession(false);
        setSessionExpiresAt(null);
        message.error(data.message || "Failed to obtain cookies");
      },
      onError: (error: any) => {
        setLoginSessionId("");
        setHasActiveLoginSession(false);
        setSessionExpiresAt(null);
        message.error(error.response?.data?.error || "Failed to obtain cookies");
      },
    }
  );

  const sendCookiesToWorkerMutation = useMutation(
    async () => {
      const response = await api.post("/settings/youtube/sync-to-worker");
      return response.data as { success: boolean; message: string };
    },
    {
      onSuccess: (data) => {
        if (data.success) {
          message.success(data.message || "Cookies sent to worker successfully");
        } else {
          message.warning(data.message || "Cookies saved but worker sync partial");
        }
        refetchStatus();
      },
      onError: (error: any) => {
        message.error(error.response?.data?.error || "Failed to send cookies to worker");
      },
    }
  );

  const handleStartLoginSession = () => {
    // Allow restarting a session even if local state was left active.
    setLoginSessionId("");
    setHasActiveLoginSession(false);
    setSessionExpiresAt(null);
    startLoginSessionMutation.mutate();
  };

  const handleObtainCookies = () => {
    if (!hasActiveLoginSession) {
      message.warning("Start a login session first");
      return;
    }

    const payload = loginSessionId ? { sessionId: loginSessionId } : {};
    obtainCookiesMutation.mutate(payload);
  };

  const handleSendToWorker = () => {
    sendCookiesToWorkerMutation.mutate();
  };

  // Get status colors
  const getStatusColor = () => {
    if (!status) return "default";
    if (status.status === "healthy") return "green";
    if (status.status === "degraded") return "orange";
    return "red";
  };

  const getStatusIcon = () => {
    if (!status) return null;
    if (status.status === "healthy") return <CheckCircleOutlined />;
    if (status.status === "degraded") return <ExclamationCircleOutlined />;
    return <CloseOutlined />;
  };

  return (
    <div style={{ padding: "24px" }}>
      <Space direction="vertical" style={{ width: "100%" }} size="large">
        {/* Header */}
        <div>
          <Title level={2}>YouTube Authentication</Title>
          <Paragraph>
            Manage cookies for YouTube stream resolution. Cookies are required for accessing
            age-restricted or member-only content.
          </Paragraph>
        </div>

        {/* Status Card */}
        {statusLoading ? (
          <Spin />
        ) : (
          status && (
            <Card>
              <Row gutter={[24, 24]}>
                <Col xs={24} sm={12} md={6}>
                  <Statistic
                    title="Status"
                    value={status.status}
                    prefix={getStatusIcon()}
                    valueStyle={{ color: getStatusColor() }}
                  />
                </Col>
                <Col xs={24} sm={12} md={6}>
                  <Statistic
                    title="Cookies File"
                    value={status.cookiesFileExists ? "Exists" : "Missing"}
                    prefix={status.cookiesFileExists ? <CheckOutlined /> : <CloseOutlined />}
                    valueStyle={{
                      color: status.cookiesFileExists ? "#52c41a" : "#f5222d",
                    }}
                  />
                </Col>
                <Col xs={24} sm={12} md={6}>
                  <Statistic
                    title="Validation"
                    value={status.cookiesValid ? "Valid" : "Invalid"}
                    prefix={status.cookiesValid ? <CheckOutlined /> : <CloseOutlined />}
                    valueStyle={{
                      color: status.cookiesValid ? "#52c41a" : "#f5222d",
                    }}
                  />
                </Col>
                <Col xs={24} sm={12} md={6}>
                  <Statistic
                    title="Cookie Count"
                    value={status.cookieCount ?? 0}
                    suffix={status.cookieCount ? " cookies" : ""}
                  />
                </Col>
              </Row>

              {status.earliestExpiration && (
                <Divider />
              )}

              {status.earliestExpiration && (
                <Row>
                  <Col xs={24}>
                    <Text strong>Earliest Expiration: </Text>
                    <Text>
                      {dayjs(status.earliestExpiration).format("YYYY-MM-DD HH:mm")}
                      {dayjs(status.earliestExpiration).isBefore(dayjs().add(7, "day"))
                        ? " ⚠️ Expires soon"
                        : ""}
                    </Text>
                  </Col>
                </Row>
              )}

              {status.authAlertActive && (
                <>
                  <Divider />
                  <Alert
                    message="Authentication Required"
                    description="Cookies are missing or invalid. Please add valid YouTube cookies to resume YouTube streaming."
                    type="error"
                    showIcon
                  />
                </>
              )}

              {status.excludedYouTubeSources && status.excludedYouTubeSources.length > 0 && (
                <>
                  <Divider />
                  <div>
                    <Text strong>Excluded YouTube Sources ({status.excludedYouTubeSources.length}):</Text>
                    <List
                      dataSource={status.excludedYouTubeSources}
                      renderItem={(source: string) => (
                        <List.Item>
                          <Badge status="error" text={source} />
                        </List.Item>
                      )}
                      style={{ marginTop: "12px" }}
                    />
                  </div>
                </>
              )}
            </Card>
          )
        )}

        <Card title="Automatic Cookie Extraction">
          <Space direction="vertical" style={{ width: "100%" }} size="middle">
            <Alert
              type="info"
              showIcon
              message="Steps"
              description={
                <div>
                  <div>1. Click Start Login Session and sign in on the opened YouTube window.</div>
                  <div>2. Return here and click Obtain Cookies.</div>
                </div>
              }
            />

            {hasActiveLoginSession ? (
              <Alert
                type="success"
                showIcon
                message="Active login session"
                description={
                  <span>
                    Session active. {sessionExpiresAt ? `Expires at ${dayjs(sessionExpiresAt).format("YYYY-MM-DD HH:mm:ss")}.` : ""}
                  </span>
                }
              />
            ) : null}

            <Space>
              <Button
                type="primary"
                size="large"
                icon={<RobotOutlined />}
                loading={startLoginSessionMutation.isLoading}
                onClick={handleStartLoginSession}
              >
                Start Login Session
              </Button>

              <Button
                size="large"
                onClick={handleObtainCookies}
                loading={obtainCookiesMutation.isLoading}
                disabled={!hasActiveLoginSession}
              >
                Obtain Cookies
              </Button>

              <Button
                size="large"
                onClick={handleSendToWorker}
                loading={sendCookiesToWorkerMutation.isLoading}
                disabled={!status?.cookiesValid}
              >
                Send to Worker
              </Button>
            </Space>

            {obtainCookiesMutation.data?.success ? (
              <Text type="success">{obtainCookiesMutation.data.message}</Text>
            ) : null}
          </Space>
        </Card>
      </Space>
    </div>
  );
};

export default YouTubeSettings;
