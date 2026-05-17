# 项目结构和职责边界

## 顶层目录

```text
chatgpt_phone_reg.py      兼容入口，转发到 sms_tool.cli
config.example.json       配置模板
config.json               本地配置，包含密钥，不提交
sessions/                 固化后的注册登录态和 PayPal 链接结果，不提交
runtime/                  Sentinel 等运行时缓存，不提交
sms_tool/                 Python 后端注册流程
SmsWorkbench/             WPF 桌面管理端
docs/                     项目说明和维护文档
```

## 后端模块

```text
sms_tool/cli.py           命令行参数、批量入口、固化文件保存
sms_tool/config.py        config.json 查找和加载
sms_tool/paths.py         项目根目录、sessions/runtime 路径解析
sms_tool/mailbox.py       邮箱账户来源、LuckMail token、Graph/IMAP OTP 收信
sms_tool/providers/       外部邮箱/OTP 服务的低层客户端
sms_tool/providers/luckmail_token.py  LuckMail token 直连接口客户端
sms_tool/registration.py  ChatGPT 邮箱注册、auth session、PayPal 链接编排
sms_tool/gen_pp_link.py   使用 accessToken 生成 PayPal 支付链接
sms_tool/utils.py         随机资料、计时、通用辅助函数
```

## 运行数据边界

- `sessions/`：只放成功注册后的固化 JSON，文件内包含 access token、cookie、邮箱 token 和 PayPal 链接，按敏感数据处理。
- `runtime/`：只放可再生成的运行缓存，例如 `sentinel_cache.json`。
- `mailbox_tokens.txt`：可选邮箱池输入文件，仍保持在项目根目录，后续如果要长期维护邮箱池，可以再迁到 `data/` 或接入自建 Outlook 管理器。

## Outlook 自建收信底座

`keh4l/outlook-mail-manager` 的结构适合作为独立邮箱管理服务参考：账户表保存 `email/password/client_id/refresh_token`，后端刷新 Microsoft OAuth token，先走 Graph API，失败时用 IMAP XOAUTH2 拉取收件箱或垃圾箱。

当前项目已经有 Graph API 收信和 LuckMail token 收信两条路径。若购买到的是完整 Outlook OAuth 凭据，应直接接入 Graph/IMAP；若只有 LuckMail 的 `tok_...`，该 token 不能直接替代 Microsoft refresh token，应通过 LuckMail token API 换取邮件内容。

## LuckMail token OTP API

LuckMail 长效邮箱购买返回的 `tok_...` / `lmp_...` 直接调用 LuckMail OpenAPI：

- `GET /api/v1/openapi/email/token/{token}/code`：返回 `data.email_address`、`data.verification_code`、`data.mail`、`data.has_new_mail`。
- `GET /api/v1/openapi/email/token/{token}/mails`：返回 `data.email_address`、`data.mails` 邮件列表。
- `GET /api/v1/openapi/email/token/{token}/alive`：返回邮箱可用性和邮箱地址。

项目里由 `sms_tool/providers/luckmail_token.py` 固化这层协议。`sms_tool/mailbox.py` 只使用 LuckMail token API 取 OTP。

## 固化购买注册流程

一条命令跑完整链路：

```powershell
python .\chatgpt_phone_reg.py --buy-luckmail-mailbox --proxy socks5h://127.0.0.1:7897
```

该命令会：

1. 调 LuckMail `POST /api/v1/openapi/email/purchase` 购买 `openai + ms_imap + outlook.com` 长效邮箱。
2. 使用返回的 `email_address` 和 `token` 创建 `MailboxAccount(provider="luckmail_token")`。
3. 走邮箱注册，验证码优先通过 `sms_tool/providers/luckmail_token.py` 直连 LuckMail token API 读取。
4. 从 `https://chatgpt.com/api/auth/session` 提取 `accessToken`。
5. 调 `sms_tool/gen_pp_link.py` 生成 PayPal 支付链接。
6. 保存到 `sessions/session_{email}_{timestamp}.json`，并写入购买信息、余额、PayPal 结果和分步耗时。

## 人工 PayPal 与 session 刷新链路

项目只负责保存和打开官方托管 PayPal 链接，不在代码内自动创建 PayPal 账号、接码、填写卡号或提交 CVV。

- CLI 通过 `--list-paypal-links` 展示 `paypal_url`、`paypal_status`、`refresh_token_status`。
- CLI 通过 `--email <account> --open-paypal-link` 打开指定账号的 PayPal 链接，后续授权或支付由人工在浏览器完成。
- CLI 通过 `--email <account> --regenerate-paypal-link` 使用现有 `access_token` 重新生成短时 PayPal 授权链接，并回写 SQLite 和 session JSON。
- 人工完成后用 `--email <account> --mark-paypal-status completed` 标记 SQLite 和 session JSON。
- `--email <account> --refresh-session` 打开可见 Playwright 浏览器，人工完成登录/授权后轮询 `https://chatgpt.com/api/auth/session`，回写新的 `access_token`、`auth_session`、`oauth_refresh_token`、`refresh_token_status`。
- WPF 侧对应按钮为“打开支付链接”“重新生成链接”“标记支付完成”“刷新Session”，列表中展示支付状态和 refresh 状态。
