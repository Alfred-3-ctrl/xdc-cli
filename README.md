# xdc-cli · 学在浙大

浙江大学学在浙大和智云课堂的 CLI，支持课件查询和导出。

## Windows 学习桌面

在仓库根目录安装依赖并启动组件。安装桌面依赖需要 Node.js 22.12 或更高版本。

```powershell
npm ci
npm --prefix desktop ci
npm --prefix desktop start
```

组件支持总览、课程详情和下载队列。它复用 CLI 的本地会话，首次登录可以选择让 Windows 加密保存统一认证凭据。设置页提供外观、窗口位置和尺寸、布局快捷键、下载目录及登录 Windows 时启动。首次运行会添加「学习桌面」开始菜单入口。

Windows 下由本机辅助进程检测 `uu.exe`。运行 UU 时组件收起，正在处理的文件完成后退出。UU 退出后组件恢复，排队任务继续。辅助进程首次运行时使用 Windows 自带的 .NET Framework 编译器构建。

- 命令：**`xdc`**（别名 `xzzd`）
- 认证：浙大统一身份认证（CAS + Keycloak）
- 依赖：Node ≥ 20.3，智云课件通过 `pdf-lib` 合成 PDF

本项目从 D32gang 的 [zju-cli/xzzd-cli](https://github.com/d32gang-steel/zju-cli/tree/d6d143fb22b1000472d1743f72620cb091e2a99d/xzzd-cli) 导入并独立维护。上游子项目在 `package.json` 中标注 MIT。接口记录见 [api.md](api.md)，Agent 用法见 [SKILL.md](SKILL.md)。

智云接入和 WPS 导出参考了 PeiPei233 的 [zju-learning-assistant](https://github.com/PeiPei233/zju-learning-assistant/tree/a759541)。WPS 适配器保留了原作者的 MIT 声明。

## 安装

```bash
cd xdc-cli
npm ci
npm link          # 或 node bin/xdc.js <命令>
```

## 登录

```bash
export XD_USERNAME=3xxxxxxxxx
export XD_PASSWORD='...'
xdc login
```

- 会话存 `~/.config/xdc-cli/session.json`（0600），约 1 小时过期；带账号密码时命令会**自动续登**。
- SSO 触发验证码/二次验证时，从浏览器 DevTools 复制 Cookie：

```bash
xdc login --cookie "session=...; route=..."
```

`xdc login --classroom` 完成学在浙大登录后，用同一统一认证会话建立智云会话。`xdc classroom login` 可使用已有统一认证会话。如果需要在智云官网完成交互验证，可以将该站点的 Cookie 请求头写入本机临时文件，再通过 `xdc classroom login --cookie-file <文件>` 导入。`xdc logout` 清除两边的本地会话。

## 命令

```bash
xdc status                     # 平台健康检查
xdc doctor                     # 会话/身份/端点自检

xdc courses [--current] [--semester 2025-2026-2] [--page N]
xdc course <id>                # 课程详情 + 章节

xdc ddl [--days 7]             # 当前学期的未提交作业和提交状态未知的作业
xdc assignments [--current] [--pending]
xdc assignment <cid> <aid>     # 作业要求（正文/附件/提交状态）
xdc assignment <cid> <aid> --download ./hw

xdc todos
xdc semesters                  # 学期 + 每学期门数
xdc grades <cid>
xdc announcements --all
xdc notifications
xdc materials <cid> [--include-homework] [--file <id>] [--download <dir>]
xdc classroom status
xdc classroom sessions [--month YYYY-MM]
xdc classroom slides <cid> <sessionId>
xdc classroom export <cid> <sessionId> --output <文件.pdf>
xdc rollcall --course <cid>    # 点名场次 + 本人状态
xdc watch [--interval 60]      # 盯点名，出现即响铃

xdc api GET /api/profile       # 原始 API（探索用）
xdc discover
```

## 示例

```bash
$ xdc ddl
找到 2 个未提交或状态未知的作业
截止              剩余        课程             作业                    提交状态
2026-11-05 23:59  9天22小时   操作系统         Lab 2: RV64 进程调度模拟 未提交
2026-11-16 23:59  20天22小时  B/S体系软件设计  中期设计文档            未知
```

```bash
$ xdc courses --current
▌ 2026-2027秋冬  ← 当前
  104394  B/S体系软件设计        胡晓军  计算机科学与技术学院
  99468   操作系统                伍赛    计算机科学与技术学院
▌ 2026-2027秋  ← 当前
  103991  工程管理专题讲座        阮伟    控制科学与工程学院
共 7 门
```

## 输出

非 TTY 自动 JSON：

```json
{"ok": true, "count": 4, "data": [...]}
{"ok": false, "error": {"code": "AUTH_REQUIRED", "message": "...", "hint": "..."}}
```

退出码：`0` 成功 · `1` 一般错误/平台异常 · `2` 未登录 · `3` 不存在 · `4` 网络 · `5` 用法。

`--json` 强制 JSON，`--compact` 单行，`--fields a,b` 裁剪字段。`--debug` 会遮盖常见的 Cookie、认证头和 URL 票据字段，响应正文仍可能包含个人信息。

`ddl --days 7` 只包含未来 7 天内到期的作业，省略 `--days` 时也会显示已逾期和无截止日期的记录。JSON 中 `submitted: null` 表示提交状态未知。`partial: true` 表示有课程查询失败，详情在 `errors` 中，`data` 仍含可用的结果。

`materials --include-homework` 会把作业附件纳入清单。`--file` 可重复指定附件 ID。下载结果中的 `format` 为 `original` 或 `preview_pdf`，预览版文件以 `.preview.pdf` 结尾。WPS 预览导出要求平台明确授权，缺少授权信息时返回 `WPS_PERMISSION_UNKNOWN`。批量结果通过 `data.partial`、`data.errors` 和 `data.failed` 报告未完成项。

智云导出将一节课堂的 JPG 或 PNG 幻灯片按顺序合成 PDF。缺页会返回 `CLASSROOM_PARTIAL_EXPORT` 和 `missing_pages`。已用真实账号验证双平台登录、课程资料和作业附件下载、WPS 原文件下载及智云 PDF 导出。实测的仅预览 WPS 样本未提供可确认的导出授权。

## 限制

- 平台经常 5xx/HTML 错误页：`xdc status` 会区分「平台挂了」和「会话失效」；
- 学生的 `/api/user` 是 403，身份来自 `/api/profile`；
- 签到接口对学生通常是 403（教师功能），`rollcall` 会返回 `available:false`；
- 学期有半学期（`2026-2027秋` = `...-1A`），`--current` 会把同基数的半学期一起算上；
- 提交作业的 `xdc submit` 接口未在真机验证，默认 dry-run。写请求遇到网络失败、限流或服务端错误时只尝试一次。

## 开发

运行 `npm test` 可执行单元测试和模拟服务器端到端测试。测试使用本地会话夹具。
