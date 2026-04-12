import { ProCard, PageContainer } from "@ant-design/pro-components";
import { FC, ReactNode } from "react";
import { Button } from "antd";
interface RouteCardProps {
  children: ReactNode;
}

const RouteCard: FC<RouteCardProps> = ({ children }) => (
  <PageContainer
    extra={[
      <Button key="3">funcionar</Button>,
      <Button key="2" type="primary">
        principal
      </Button>,
    ]}
    subTitle="descripción sencilla"
    footer={[
      <Button key="3">reiniciar</Button>,
      <Button key="2" type="primary">
        entregar
      </Button>,
    ]}
  >
    <ProCard
      style={{
        minHeight: 800,
        height: "100%",
      }}
      gutter={{ xs: 8, sm: 16, md: 0 }}
    >
      {children}
    </ProCard>
  </PageContainer>
);

export default RouteCard;
