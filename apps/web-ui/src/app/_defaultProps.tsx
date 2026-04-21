import {
  //ChromeFilled,
  CrownFilled,
  //SmileFilled,
  TabletFilled,
} from "@ant-design/icons";

export default {
  title: "CHR Divulgar",
  route: {
    path: "/",
    routes: [
      {
        path: "/dashboard",
        name: "Dashboard",
        icon: <CrownFilled />,
      },
      {
        path: "/notes",
        name: "Notas",
        icon: <TabletFilled />,
      } /* 
      {
        path: "/alerts",
        name: "Alertas",
        icon: <SmileFilled />,
      }, */,
      /* {
        path: "/admin",
        name: "Admin",
        icon: <CrownFilled />,
        access: "canAdmin",
        routes: [
          {
            path: "/admin/sub-page1",
            name: "admin sub-page1",
            icon: "https://gw.alipayobjects.com/zos/antfincdn/upvrAjAPQX/Logo_Tech%252520UI.svg",
          },
        ],
      },
      {
        name: "Lista",
        icon: <TabletFilled />,
        path: "/list",
        routes: [
          {
            path: "/list/sub-page",
            name: "list sub-page",
            icon: <CrownFilled />,
            routes: [
              {
                path: "sub-sub-page1",
                name: "sub-sub-page1",
                icon: <CrownFilled />,
              },
            ],
          },
        ],
      },
      {
        path: "https://ant.design",
        name: "Ant Design ",
        icon: <ChromeFilled />,
      }, */
    ],
  },
  bgLayoutImgList: [
    {
      src: "https://img.alicdn.com/imgextra/i2/O1CN01O4etvp1DvpFLKfuWq_!!6000000000279-2-tps-609-606.png",
      left: 85,
      bottom: 100,
      height: "303px",
    },
    {
      src: "https://img.alicdn.com/imgextra/i2/O1CN01O4etvp1DvpFLKfuWq_!!6000000000279-2-tps-609-606.png",
      bottom: -68,
      right: -45,
      height: "303px",
    },
    {
      src: "https://img.alicdn.com/imgextra/i3/O1CN018NxReL1shX85Yz6Cx_!!6000000005798-2-tps-884-496.png",
      bottom: 0,
      left: 0,
      width: "331px",
    },
  ],
};
