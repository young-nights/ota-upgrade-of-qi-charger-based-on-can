# 仓库代理规则

## 提交推送规则

- 每次代码修改后，强制 `git add -A` 全工程提交，不留残留文件。
- 自动流程：`git pull` → edit → `git add -A` → `git commit` → `git push`。
- 仓库下所有变更（代码、文档、配置）必须一并提交推送。
- **提交说明格式**：commit message 的 summary 简洁概括，但必须在 Description 中详细描述变更内容，按模块/文件分点列出具体改动，包含关键地址、常量、新增函数名等技术细节。示例：
  - 1. Device Info 结构扩展（device_info.h/c）：新增 ecdsa_pubkey[65] 存储 SEC1 未压缩公钥...
  - 2. Bootloader 读公钥逻辑（boot_verify.c）：优先从 Device Info 读取...
  - 3. APP UDS 服务（can_protocol.c/h）：新增 DID 0x2120 读写公钥...
