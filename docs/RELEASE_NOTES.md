# Release Notes

## v0.1.0 — 2026-03-22

首个公开整理版本，完成从本地诊断工具到可独立发布项目仓库的整理。

### Highlights
- 新增 **应用出口监控**，覆盖：
  - TeamViewer
  - 飞书 / Feishu
  - 百度网盘 / Baidu Netdisk
  - OpenClaw / Telegram API
- 新增 **双策略路由刷新**：
  - 本地优先流量固定到本地网关
  - Telegram / OpenClaw 流量固定到 VPN 网关
- 增强 **Telegram 路由稳定性**：
  - 支持实时捕获 Telegram API IP
  - 增加常见 Telegram 网段的 VPN 路由覆盖
- 新增 **VPN / 非 VPN 公网 IP 与综合地区识别**
  - 通过多源结果综合判断地区
  - 优先把常见香港节点更合理地识别为 `Hong Kong`
- 新增 **场景化测速**：
  - 非 VPN 速度（speedtest.cn 场景）
  - VPN 速度（fast.com 场景）
- UI 调整：
  - 拆分为 `总览` / `应用出口监控` 两个 tab
  - 改善滚动区域与摘要信息布局

### Repository / Documentation
- 项目已整理为独立 Git 仓库
- README 已补充：功能、运行方式、目录结构、注意事项
- 增加示例截图，便于快速理解工具界面

### Known Limitations
- 当前测速仍为“近似场景测速”，并非真实网页内测速
- Telegram 稳定性除本地路由外，还受 VPN 节点质量影响
- 综合地区来自多源 IP 数据，不等于绝对物理位置

### Suggested Next Milestones
- 真实 `fast.com` / `speedtest.cn` 浏览器测速
- Telegram 历史连接 / IP 切换追踪
- 应用卡片按“本地优先 / VPN优先”分组
- 托盘运行与历史趋势记录
