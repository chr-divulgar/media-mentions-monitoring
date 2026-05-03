import React, { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Card, Button, Space, Layout, Divider, App } from "antd";
import { GoogleOutlined, WindowsOutlined } from "@ant-design/icons";
import {
  signInWithGoogle,
  signInWithMicrosoft,
} from "../../services/authService";
import { useAuth } from "../../context/AuthContext";
import styles from "./LoginPage.module.css";

const { Content } = Layout;

const LoginPage: React.FC = () => {
  const navigate = useNavigate();
  const { user } = useAuth();
  const { message } = App.useApp();
  const [googleLoading, setGoogleLoading] = useState(false);
  const [microsoftLoading, setMicrosoftLoading] = useState(false);

  // Si ya está autenticado, redirigir al dashboard
  useEffect(() => {
    if (user) {
      navigate("/dashboard");
    }
  }, [user, navigate]);

  const handleGoogleLogin = async () => {
    try {
      setGoogleLoading(true);
      await signInWithGoogle();
      // onAuthStateChanged dispara y el useEffect de 'user' redirige al dashboard
    } catch (error) {
      const errorMessage =
        error instanceof Error ? error.message : "Failed to login with Google";
      message.error(errorMessage);
    } finally {
      setGoogleLoading(false);
    }
  };

  const handleMicrosoftLogin = async () => {
    try {
      setMicrosoftLoading(true);
      await signInWithMicrosoft();
      // onAuthStateChanged dispara y el useEffect de 'user' redirige al dashboard
    } catch (error) {
      const errorMessage =
        error instanceof Error
          ? error.message
          : "Failed to login with Microsoft";
      message.error(errorMessage);
    } finally {
      setMicrosoftLoading(false);
    }
  };

  return (
    <Layout style={{ minHeight: "100vh" }}>
      <Content
        style={{
          display: "flex",
          justifyContent: "center",
          alignItems: "center",
          minHeight: "100vh",
          background: "linear-gradient(135deg, #667eea 0%, #764ba2 100%)",
        }}
      >
        <Card
          style={{
            width: "100%",
            maxWidth: "400px",
            borderRadius: "8px",
            boxShadow: "0 4px 6px rgba(0, 0, 0, 0.1)",
          }}
        >
          <div className={styles.loginContainer}>
            <h1 style={{ textAlign: "center", marginBottom: "8px" }}>
              Media Mentions Monitoring
            </h1>
            <p
              style={{
                textAlign: "center",
                color: "#666",
                marginBottom: "32px",
              }}
            >
              Sign in to your account
            </p>

            <Space direction="vertical" style={{ width: "100%" }} size="large">
              <Button
                type="primary"
                size="large"
                icon={<GoogleOutlined />}
                loading={googleLoading}
                onClick={handleGoogleLogin}
                block
                style={{
                  backgroundColor: "#4285F4",
                  borderColor: "#4285F4",
                  fontSize: "16px",
                  height: "40px",
                }}
              >
                {googleLoading ? "Signing in..." : "Sign in with Google"}
              </Button>

              <Button
                type="primary"
                size="large"
                icon={<WindowsOutlined />}
                loading={microsoftLoading}
                onClick={handleMicrosoftLogin}
                block
                style={{
                  backgroundColor: "#0078D4",
                  borderColor: "#0078D4",
                  fontSize: "16px",
                  height: "40px",
                }}
              >
                {microsoftLoading ? "Signing in..." : "Sign in with Microsoft"}
              </Button>
            </Space>

            <Divider style={{ margin: "24px 0" }} />

            <p style={{ textAlign: "center", color: "#999", fontSize: "12px" }}>
              By signing in, you agree to our Terms of Service
            </p>
          </div>
        </Card>
      </Content>
    </Layout>
  );
};

export default LoginPage;
