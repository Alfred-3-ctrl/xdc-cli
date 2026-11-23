# 原生玻璃原型

运行 `Xdc.NativeGlass.exe` 会打开一个合成图案测试窗口。需要 Windows 11 22H2 或更新版本，以及 x64 .NET 10 Desktop Runtime。

原型使用 Windows Composition 的 `HostBackdropBrush` 读取玻璃窗口背后的实时合成画面，模糊和染色都位于同一个圆角几何裁剪内。圆角在 16–36 DIP 范围内实时调整。视觉层由原型自己的 Win32 窗口承载。原型没有截屏功能，也不读取学习桌面的会话或课件。

## 构建

在此目录使用 .NET 10 SDK 构建单文件 EXE。

```powershell
dotnet publish .\NativeGlass.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=embedded -o .\bin\prototype
```

## 验收

1. 保持「实时模糊」开启，在图案移动或切换时观察玻璃内的颜色变化。
2. 暂停测试图案，切换「实时模糊」，比较玻璃内的细条纹与原图案。
3. 在 16、30 和 36 DIP 下检查四角。圆弧外的图案应与关闭效果时一致，圆弧内的图案应继续实时模糊。

本机在 Windows 11 25H2、150% 缩放下完成了上述验证。保持同一个玻璃窗口和模糊层，切换背后图案后，中央区域的 RGB 通道平均变化为 28.11。关闭模糊后，细条纹恢复清晰。在三个半径下合计比较了 3,560 个位于圆弧外的像素，全部与无玻璃时的测试背景一致。已验证的原型使用合成图案，实际学习桌面的接入和跨显示器行为仍待单独验收。

## 本机运行和签名

本次产物未签名，在本机的智能应用控制开启时已正常启动。这个结果只覆盖本机和本次生成的文件。正式分发可用受系统信任的代码签名证书，为单文件 EXE 加上带 RFC 3161 时间戳的 SHA-256 Authenticode 签名。本机当前没有这样的签名证书和私钥。

```powershell
signtool sign /fd SHA256 /sha1 CERT_THUMBPRINT /tr RFC3161_TIMESTAMP_URL /td SHA256 .\bin\prototype\Xdc.NativeGlass.exe
signtool verify /pa /v .\bin\prototype\Xdc.NativeGlass.exe
```

如果其他设备出现启动拦截，可核对 `Microsoft-Windows-CodeIntegrity/Operational` 中与 EXE 路径对应的 3077 事件。签名通过后仍需正常启动并做视觉验收。 [微软签名说明](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control) · [签名与事件日志验证](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control)
