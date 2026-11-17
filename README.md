# xdc-cli · 学在浙大

浙江大学课程平台 [`courses.zju.edu.cn`](https://courses.zju.edu.cn)（学在浙大 / TronClass）的 CLI。

- 命令：**`xdc`**（别名 `xzzd`）
- 认证：浙大统一身份认证（CAS + Keycloak）
- 依赖：Node ≥ 20，无第三方包

本项目从 D32gang 的 [zju-cli/xzzd-cli](https://github.com/d32gang-steel/zju-cli/tree/d6d143fb22b1000472d1743f72620cb091e2a99d/xzzd-cli) 导入并独立维护。上游子项目在 `package.json` 中标注 MIT。接口记录见 [api.md](api.md)，Agent 用法见 [SKILL.md](SKILL.md)。

## 安装

```bash
cd xdc-cli
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
xdc materials <cid> [--download <dir>]
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

## 限制

- 平台经常 5xx/HTML 错误页：`xdc status` 会区分「平台挂了」和「会话失效」；
- 学生的 `/api/user` 是 403，身份来自 `/api/profile`；
- 签到接口对学生通常是 403（教师功能），`rollcall` 会返回 `available:false`；
- 学期有半学期（`2026-2027秋` = `...-1A`），`--current` 会把同基数的半学期一起算上；
- 提交作业的 `xdc submit` 接口未在真机验证，默认 dry-run。写请求遇到网络失败、限流或服务端错误时只尝试一次。

## 开发

运行 `npm test` 可执行单元测试和模拟服务器端到端测试。测试使用本地会话夹具。
