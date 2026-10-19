# 狒狒食柿·独立试用版


## 下载与使用

从 [Releases](https://github.com/Xiao-Yanjiu/FeifeiShisi-Standalone/releases) 下载 `FeifeiShisi-Standalone-0.1.2.zip`，完整解压。

1. 在电脑运行 `免安装服务端/狒狒食柿-试用版.exe`，保留旁边的“组件”目录。
2. 启动FF14，登录角色，在服务端选择对应游戏窗口并连接。多开时按角色与 PID 选择；服务端与游戏须处于相同的 Windows 权限级别。
3. 手机安装同一包内的 `狒狒食柿-试用版.apk`，填写电脑局域网地址及默认端口 18766，在电脑确认配对码。
4. 游戏串流协议在服务端选择 TCP 或 UDP。UDP 模式需要网络允许对应 UDP 端口；公网使用还需自行配置映射。
5. 自动连接、静默启动、开机自启动、防掉线按需开启。关闭设置窗口会进入托盘，完全退出请用托盘菜单。

APK 包名为 `cn.xiaoyanjiu.ff14chat.trial`。没有检测更新功能，后续手动安装同签名 APK 升级。发布包不含签名私钥或个人配对数据。

## 保留与移除

保留聊天、角色与物品数据、队伍与原生招募、游戏画面串流和手机控制、鱼糕提醒、灰机 Wiki、石之家浏览，以及原版设置界面和推送业务适配。

## 源码与构建

- `desktop`：Windows 桌面宿主、游戏数据及传输适配。
- `client`：Android App，沿用原版 HTML/CSS/Java 界面。
- `original-port/server`：原版业务与 ImGui 设置代码。
- `original-port/desktop-host`：桌面防掉线适配。
- `native`：原生桥接、MinHook、测试夹具；提供对应预编译 `TrialBridge.dll`。
- `stream-host`：独立进程窗口采集与 H.264 编码。
- `profile`：读取游戏 EXE 生成匹配配置。
- `tests`、`original-port/tests`：协议、界面、输入和业务检查。

Windows 服务端需 .NET 10 SDK：运行 `build-server.ps1`。自包含产物内部名为 `FF14Chat.Trial.Server.exe`，发布时命名为 `狒狒食柿-试用版.exe`，并在旁边放置发布包中的完整“组件”目录。

原生桥接使用 Zig 0.15.2：`./build-native.ps1 -Zig <zig.exe>`。

Android：运行 `client/build.ps1`。`YANJIU_BUILD_TOOLS` 指向包含 JDK 17、build-tools、platform/android-35/android.jar 和 zxing.jar 的工具目录。未配置签名变量时生成未签名 APK；签名需自行提供 `YANJIU_KEYSTORE`、`YANJIU_KEY_ALIAS`、`YANJIU_KEYSTORE_PASSWORD` 和 `YANJIU_KEY_PASSWORD`，不能用自己的签名覆盖官方试用版 APK。

真实窗口测试需已登录游戏，并通过环境变量提供本地工具路径；不应在无人监督时执行聊天、宏或队伍变更测试。

## 来源与许可

基于 [Xiao-Yanjiu/OmniToolbox.xiaoyanjiu](https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu)，参考下载提交 `fa0dec27b443f732cc72640ebb7ce9ebfa824333`。本项目源码按 **GPL-3.0-or-later** 提供，见 [LICENSE](LICENSE)。

MinHook、QRCoder、ZXing、ImGui 和 .NET 等依赖遵循各自许可。发布包保留对应许可证；NapCat 官方包未修改，适用其完整有限再分发许可与非商业使用条件；中文字体采用 SIL OFL。游戏图标和数据版权归各自权利人所有，不纳入本项目 GPL 授权。
