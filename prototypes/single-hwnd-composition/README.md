# 单窗口 Composition 实验

此原型以一棵 `Windows.UI.Composition` 视觉树呈现圆角背景、滚动视口、列表及前景控件。列表字形渲染到预乘 Alpha 表面，通过 D3D11 上传。滚动时移动列表视觉对象，已上传的字形可继续复用。空白区域由同一个窗口处理命中。

原型使用模拟数据。它适合比较渲染、命中和开销，桌面常驻、登录自启、业务和无障碍由正式宿主实现。当前每行保留自己的表面，实际产品需要评估视口虚拟化。自测报告中的耗时仅覆盖本线程分发滚轮消息和更新位置属性。

在 Windows 11 和 .NET 10 上从仓库根目录构建。普通运行时，右键点击原型关闭。传入 `--self-test` 则执行内置交互自测并退出。

```powershell
$prototype = Join-Path $env:TEMP ('single-window-glass-' + [guid]::NewGuid())
dotnet publish ./prototypes/single-hwnd-composition/SingleWindowGlass.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $prototype
$executable = Join-Path $prototype 'Xdc.SingleWindowGlass.exe'
Start-Process -FilePath $executable -ArgumentList '--self-test' -Wait -PassThru
```

需要交付给开启智能应用控制的机器时，对暂存构建签名并通过正常的 EXE 入口验证。 [签名说明](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
