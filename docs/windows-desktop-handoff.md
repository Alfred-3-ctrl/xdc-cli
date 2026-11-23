# 学习桌面工程复核报告

## Gemini 的实现

原生路线继续用 WinForms 管控件和交互，背景窗用 DWM 合成模糊和透明染色，上方再加一个不接收鼠标的逐像素 Alpha 文字窗。字形和边缘透明度绘制在同一块预乘 Alpha 缓冲区，滚动时更新可视区。这样能保留中文灰度抗锯齿，也能保留透明行的点击区域。课程标题加重，教师和学期使用更小的次级文字。 [Microsoft — UpdateLayeredWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow)

网页评审原型和 Electron 前端采用预设色块配合精确 HEX、自定义 HSV 和屏幕吸管。确认只提交颜色草稿，取消恢复打开弹窗时的草稿，外观页保存才落盘。原生宿主同时接入预设色、实时模糊和纯透明选项，旧配置缺少模糊字段时继续使用实时模糊。

另一条路线把背景、圆角、前景和滚动的表面放进一个 HWND。字形上传到 D3D11 支撑的合成表面，滚动只更新视觉对象的位置，透明区命中由同一个窗口处理。Gemini 报告的 0.22 ms 是 60 次消息分发的总耗时，实际呈现帧率需要另测。该方案作为实验保留，仍需完成视口虚拟化、无障碍和桌面生命周期等产品工作。 [Microsoft — Composition Drawing Surface interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.composition.interop/nn-windows-ui-composition-interop-icompositiondrawingsurfaceinterop)

## 本轮补正

- 原生列表的悬停高亮使用同一 Alpha 面，保留玻璃穿透。指针移出时清除高亮。移除了将产品文字层输出为图片的临时调试代码。
- Electron 修复了预设色与 CSP 的兼容、HEX 与吸管值的精度，以及取消后异步取色污染下一次弹窗的问题。纯透明设置可与原生版互通。
- 关闭原生自启时清理归属本应用的旧启动项。原生启动入口已设置后，Electron 预览保留该归属。当前用户的 Windows 登录启动和开始菜单入口已核对并指回原生程序，系统的启动批准状态由 Windows 管理。

## 验收和发布边界

在启用智能应用控制的 Windows 上，通过原生候选 EXE 的正常入口测试了两组背景上的文字抗锯齿、透明区域点击和取色弹窗的真实窗口层级。150% DPI 下，16 和 36 DIP 两档圆角共检查了 8,496 个角外像素，均为下层背景。实时模糊和纯透明均可预览并保存，课程资源和账户使用隔离模拟数据。

Windows 的自启语义为当前用户登录后启动。此次核对了实际启动文件夹、目标、启动参数、工作目录、AppUserModelID 和系统批准状态。真实注销或重启后的完整时序须由后续登录观察确认。独立单窗口原型通过 Release 编译，当前机器拒绝了该原型新 EXE 的普通启动，其交接性能数据保留为参考。签名与正常启动的复现流程见 [智能应用控制开发说明](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview)。

## 复用边界

学习桌面继续以低内存原生宿主为主。网页设计原型和单窗口实验都有独立目录。私有模板提取了 WinForms 组件宿主、文字层、通透滚动与点击、设置、屏幕吸管、窗口定位及可选的登录启动，并使用独立身份及模拟事项。CLI、校园业务、会话、课程数据和原有配置留在学习桌面项目侧。
