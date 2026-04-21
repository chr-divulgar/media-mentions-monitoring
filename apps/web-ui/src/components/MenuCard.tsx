import { CaretDownFilled } from "@ant-design/icons";

import { css } from "@emotion/css";
import { Divider, Popover, theme } from "antd";
import { v4 as uuidv4 } from "uuid";

import List from "./List";
import { useNavigate } from "react-router-dom";

const MenuCard = () => {
  const { token } = theme.useToken();
  const navigate = useNavigate();
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
      }}
    >
      <Divider
        style={{
          height: "1.5em",
        }}
        type="vertical"
      />
      <Popover
        placement="bottom"
        overlayStyle={{
          width: "calc(100vw - 24px)",
          padding: "24px",
          paddingTop: 8,
          height: "307px",
          borderRadius: "0 0 6px 6px",
        }}
        content={
          <div style={{ display: "flex", padding: "32px 40px" }}>
            <div style={{ flex: 1 }}>
              <List title="soluciones financieras" />
              <List
                title="Otras soluciones"
                style={{
                  marginBlockStart: 32,
                }}
              />
            </div>

            <div
              style={{
                width: "308px",
                borderInlineStart: "1px solid " + token.colorBorder,
                paddingInlineStart: 16,
              }}
            >
              <div
                className={css`
                  font-size: 14px;
                  color: ${token.colorText};
                  line-height: 22px;
                `}
              >
                Productos populares
              </div>
              {new Array(3).fill(1).map(() => {
                return (
                  <div
                    key={uuidv4()}
                    className={css`
                      border-radius: 4px;
                      padding: 16px;
                      margin-top: 4px;
                      display: flex;
                      cursor: pointer;
                      &:hover {
                        background-color: ${token.colorBgTextHover};
                      }
                    `}
                    onClick={() => navigate("/alerts")}
                  >
                    <img src="https://gw.alipayobjects.com/zos/antfincdn/6FTGmLLmN/bianzu%25252013.svg" />
                    <div
                      style={{
                        marginInlineStart: 14,
                      }}
                    >
                      <div
                        className={css`
                          font-size: 14px;
                          color: ${token.colorText};
                          line-height: 22px;
                        `}
                      >
                        CHR Divulgar
                      </div>
                      <div
                        className={css`
                          font-size: 12px;
                          color: ${token.colorTextSecondary};
                          line-height: 20px;
                        `}
                      >
                        El lenguaje de diseño de UI más famoso de Hangzhou
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        }
      >
        <div
          style={{
            color: token.colorTextHeading,
            fontWeight: 500,
            cursor: "pointer",
            display: "flex",
            gap: 4,
            paddingInlineStart: 8,
            paddingInlineEnd: 12,
            alignItems: "center",
          }}
          className={css`
            &:hover {
              background-color: ${token.colorBgTextHover};
            }
          `}
        >
          <span> Centro de activos de nivel empresarial</span>
          <CaretDownFilled />
        </div>
      </Popover>
    </div>
  );
};

export default MenuCard;
