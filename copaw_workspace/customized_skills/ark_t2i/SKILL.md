---
name: ark_t2i
description: "Text-to-image generation via Volcengine Ark (Seedream/Doubao). Generates images using ARK_* env vars and saves them to ARK_T2I_OUT_DIR."
metadata:
  {
    "custom_skill_version": "1.0",
    "requires": {
      "env": ["ARK_API_KEY", "ARK_BASE_URL", "ARK_T2I_MODEL"]
    }
  }
---

# ark_t2i (Ark 文生图)

用火山方舟 Ark v3 的图片生成能力（Seedream / Doubao 模型）做 **文生图**。

- 默认从环境变量读取配置：
  - `ARK_API_KEY`：Ark API Key
  - `ARK_BASE_URL`：例如 `https://ark.cn-beijing.volces.com/api/v3`
  - `ARK_T2I_MODEL`：例如 `doubao-seedream-4-5-251128`
  - `ARK_T2I_OUT_DIR`：输出目录（可选，默认 `./images`）
  - `ARK_T2I_SIZE`：尺寸（可选，默认 `2K`）

## 用法（给妲己一句话就行）

你只要描述想要的画面，例如：

- “生成一张赛博朋克雨夜街头，霓虹灯反射在积水里，电影感，2K”
- “画一张白底产品海报风格的茶杯，柔光摄影棚，极简”

妲己会调用脚本：
- 生成图片
- 下载并保存到 `ARK_T2I_OUT_DIR`
- 回你保存路径（方案 A）

## 脚本

- `scripts/generate.py`：核心生成脚本（CLI / 可被工具调用）

## 备注

- 本 skill 不会把 `ARK_API_KEY` 写入文件。
- 你在聊天里发过明文 key，建议去控制台旋转（换新 key）。
