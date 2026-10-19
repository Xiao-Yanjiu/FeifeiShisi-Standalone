# 狒狒食柿·独立试用版

面向不使用卫月（Dalamud）的 FF14 国服玩家。基于原版狒狒食柿源码做 Windows 桌面适配，沿用原版服务端与手机 App 界面，移除依赖卫月的页面与功能。

当前版本：**0.1.2**。服务端为 Windows x64 免安装版，手机端为 Android APK；完整运行组件已随发布包提供。

## 下载与使用

从 [Releases](https://github.com/Xiao-Yanjiu/FeifeiShisi-Standalone/releases) 下载 `FeifeiShisi-Standalone-0.1.2.zip`，完整解压。

1. 在电脑运行 `免安装服务端/狒狒食柿-试用版.exe`，保留旁边的“组件”目录。
2. 启动不加载卫月的 FF14，登录角色，在服务端选择对应游戏窗口并连接。多开时按角色与 PID 选择；服务端与游戏须处于相同的 Windows 权限级别。
3. 手机安装同一包内的 `狒狒食柿-试用版.apk`，填写电脑局域网地址及默认端口 18766，在电脑确认配对码。
4. 游戏串流协议在服务端选择 TCP 或 UDP。UDP 模式需要网络允许对应 UDP 端口；公网使用还需自行配置映射。
5. 自动连接、静默启动、开机自启动、防掉线按需开启。关闭设置窗口会进入托盘，完全退出请用托盘菜单。

APK 包名为 `cn.xiaoyanjiu.ff14chat.trial`，与卫月版独立安装和存储。没有检测更新功能，后续手动安装同签名 APK 升级。发布包不含签名私钥或个人配对数据。

## 保留与移除

保留聊天、角色与物品数据、队伍与原生招募、游戏画面串流和手机控制、鱼糕提醒、灰机 Wiki、石之家浏览，以及原版设置界面和推送业务适配。

移除国服脱机频道、国服离线账号与权限、国服相关聊天和招募板、卫月系统通知、Omni 页面、石之家导入幻化及应用检测更新。

## 本版本修复与验证

- 手机在收到 SPS/PPS 和完整关键帧后初始化 H.264 解码器；TCP/UDP 丢帧或重连后保留并补齐编码参数。
- 浏览器外壳与系统栏跟随 App 主题；保留原页面，不因切换主题重载网站。
- 服务端空状态不再占据一整行，缩小折叠分组之间的留白。
- 真实游戏 TCP 取帧、CPU 编码与 UDP 经手机 Java 协议握手/重组均已验证；用户已确认手机串流可以使用。
- 54 项托管检查、14 项原版输入检查、6 项原版界面检查、6 项 Java 视频协议检查及五种浏览器外壳主题检查通过。

仍属试用版本：连接之前的游戏缓冲区聊天历史尚未接入；长时间防掉线、QQ 登录和真实聊天/宏/队伍变更等操作尚未逐项完成长期实测。已连接期间采集的聊天历史会本地持久化。

当前游戏 EXE SHA-256：`958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9`。游戏更新后需重新核对结构和配置；不要仅绕过版本校验。

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
