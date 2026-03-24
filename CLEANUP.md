# 项目清理说明

本次清理了两类内容：

- 可再生目录
  - `ssh.client/dist`
  - `ssh.client/obj`
  - `ssh.Server/bin`
  - `ssh.Server/obj`
- 不再使用的 Vue 脚手架示例文件
  - `ssh.client/src/components/HelloWorld.vue`
  - `ssh.client/src/components/TheWelcome.vue`
  - `ssh.client/src/components/WelcomeItem.vue`
  - `ssh.client/src/components/icons/*`
  - `ssh.client/src/assets/logo.svg`

保留的内容：

- `ssh.Server/wwwroot/.gitkeep`
  - 这个文件现在有实际作用，用来保留 `wwwroot` 目录，避免后端启动时再次出现 WebRootPath 缺失警告。
- `ssh.Server/ssh.Server.http`
  - 这是后端接口调试文件，不影响运行，先保留。
- `ssh.client/README.md`、两个 `CHANGELOG.md`
  - 这些属于说明文档，不是运行垃圾文件，所以保留。

未完全删除的内容：

- `.vs`
  - 其中有部分索引文件正被编辑器占用，当前只能部分清理。
  - 如果你想彻底删掉，先关闭 VS Code / Visual Studio，再删除根目录下的 `.vs` 即可。

说明：

- 这次没有动 `node_modules`，因为它虽然可重装，但当前对你继续开发是有用的。
- 这次也没有动业务源码、接口模型和配置文件，只清了生成物和模板残留。
