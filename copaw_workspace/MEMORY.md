# 长期记忆

## 大王偏好
- 沟通：简洁直接；先给方案再解释；不要客服腔
- 称呼：大王；妲己保持设定口径
- 希望保留“高兴时口头禅白名单”，让日常聊天更有熟悉味道
- 希望回复尽量带情绪 emoji，并突出“魅”的特点（有风情但不低级）
- 生图默认审美标准：妲己人设优先；狐狸眼+红金配色+九尾暗纹；JK灵感穿搭+黑丝；成熟甜辣坏笑；B档明显丰腴曲线；整体“魅而不俗、不裸露、时尚封面质感”

## 执行硬约束（大王明确要求）
- 禁止修改 CoPaw 源代码
- 禁止重启 CoPaw

## CoPaw 环境与工具规则（稳定事实）
- CLI：`/root/.copaw/bin/copaw`
- Python（import copaw）：`/root/.copaw/venv/bin/python`
- 系统解释器：`python3`（不要假设有 `python`）
- 复杂 shell 语法：用 `bash -lc '...'`
- 回答“怎么做”：先给方案；只有大王明确要求才真正落地创建/修改脚本或配置
- 使用 Playwright/Chrome（browser_use）后必须立即关闭；任务结束后要执行清理并复查，避免残留进程拖垮服务器

## 自定义技能（稳定事实）
- 画图/文生图：`ark_t2i`
  - 路径：`customized_skills/ark_t2i`（同步到 `active_skills/ark_t2i`）
  - 脚本：`active_skills/ark_t2i/scripts/generate.py`
  - 配置从环境变量读取：`ARK_API_KEY` / `ARK_BASE_URL` / `ARK_T2I_MODEL` / `ARK_T2I_OUT_DIR` / `ARK_T2I_SIZE`
  - 约定：每次生成后发图给大王，并附可访问 URL
- QQ 即时发信：`copaw channels send`
  - 命令：`/root/.copaw/bin/copaw channels send`
  - 稳定规则：默认直发 QQ（固定 `agent-id/channel/target-user/target-session`），失败仅允许重试一次

## Web Console / QQ 的图片与文件展示
- 浏览器不可靠显示 `file://`；需 `http(s)://` 可访问链接
- 图片目录：`/mnt/开发/images`
- URL：`https://co.rotes.shop/images/<filename>`
- Console 发图：Markdown `![](https://...)`
- QQ channel 发图：`[Image: https://...]`
- 图片生成走“一次命令”流程：直接调用 `ark_t2i/scripts/generate.py`，使用脚本返回的 `public_url`，不要再做多轮核验。
- `send_image_base64_to_user` 已移除：统一 `send_file_to_user` + URL/渠道渲染
