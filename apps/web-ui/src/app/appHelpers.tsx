import {
  GithubFilled,
  InfoCircleFilled,
  LogoutOutlined,
  QuestionCircleFilled,
} from "@ant-design/icons";

import type { MenuDataItem } from "@ant-design/pro-components";
import { SiderMenuProps } from "@ant-design/pro-layout/es/components/SiderMenu/SiderMenu";
import { GlobalHeaderProps } from "@ant-design/pro-layout/es/components/GlobalHeader";
import { AvatarProps, Dropdown } from "antd";
import { ReactNode } from "react";
import { Link } from "react-router-dom";
import SearchInput from "../components/SearchInput";
import MenuCard from "../components/MenuCard";

export function headerTitleRender(
  logo: React.ReactNode,
  title: React.ReactNode,
  props: GlobalHeaderProps,
) {
  const defaultDom = (
    <button
      style={{
        background: "none",
        border: "none",
        cursor: "pointer",
        display: "flex",
        alignItems: "center",
      }}
    >
      {logo}
      {title}
    </button>
  );

  if (
    typeof window === "undefined" ||
    document.body.clientWidth < 1400 ||
    props.isMobile
  ) {
    return defaultDom;
  }

  return (
    <>
      {defaultDom}
      <MenuCard />
    </>
  );
}

export function menuFooterRender(props?: SiderMenuProps) {
  if (props?.collapsed) return undefined;

  return (
    <div
      style={{
        textAlign: "center",
        paddingBlockStart: 12,
      }}
    >
      <div>© 2021 Made with love</div>
      <div>by Ant Design</div>
    </div>
  );
}

export function menuItemRender(
  item: MenuDataItem,
  dom: React.ReactNode,
  setPathname: React.Dispatch<React.SetStateAction<string>>,
) {
  return (
    <Link
      to={`${item.path}`}
      onClick={() => setPathname(item.path ?? "/welcome")}
    >
      {dom}
    </Link>
  );
}

export function getActionsRender(props: GlobalHeaderProps) {
  if (props.isMobile) return [];
  if (typeof window === "undefined") return [];
  return [
    props.layout !== "side" && document.body.clientWidth > 1400 ? (
      <SearchInput />
    ) : undefined,
    <InfoCircleFilled key="InfoCircleFilled" />,
    <QuestionCircleFilled key="QuestionCircleFilled" />,
    <GithubFilled key="GithubFilled" />,
  ];
}

export function getAvatarProps(): AvatarProps & {
  title: ReactNode;
  render: (props: AvatarProps, defaultDom: ReactNode) => ReactNode;
} {
  return {
    src: "https://gw.alipayobjects.com/zos/antfincdn/efFD%24IOql2/weixintupian_20170331104822.jpg",
    size: "small",
    title: "Ecopetrol",
    render: (_props: AvatarProps, dom: ReactNode) => {
      return (
        <Dropdown
          menu={{
            items: [
              {
                key: "logout",
                icon: <LogoutOutlined />,
                label: "Salir",
              },
            ],
          }}
        >
          {dom}
        </Dropdown>
      );
    },
  };
}
