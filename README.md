# CNGoldenLink

CN 金榜的 Celeste 联动 Mod，版本 0.1。

在 Mod 菜单开启连接后，通过浏览器登录 CN 金榜并授权，默认每 5 秒同步当前状态、CCT 统计和地图死亡数据。同步间隔可在菜单调整，服务地址可通过设置文件的 `ServiceBaseUrl` 修改。

无金完整通关最少死亡始终在本地记录；连接开关只控制上传，重新连接后补传已保存的纪录。当前支持 Windows，依赖版本见 `everest.yaml`。

## 构建

需要 .NET 8 SDK、安装 Everest 的 Celeste，以及配置版本的 CCT。先启动一次游戏，生成 CCT 程序集缓存。

默认将项目放在 Celeste 游戏目录下，在项目目录执行：

```powershell
dotnet build -c Release
./scripts/package.ps1
```

其他目录通过参数指定游戏路径：

```powershell
./scripts/package.ps1 -CelestePath "E:/SteamLibrary/steamapps/common/Celeste"
```

生成的 `artifacts/CNGoldenLink-0.1.0.zip` 放入游戏 `Mods` 目录，移走旧版本后启动游戏。安装包不包含游戏或 CCT 程序集。

运行测试：

```powershell
dotnet run --project tests/Diagnostics.Tests.csproj -c Release
dotnet run --project sync-tests/Sync.Tests.csproj -c Release
```

## 自动发布

GitHub Actions 在推送版本标签时运行测试、构建并发布 Release。标签必须指向 main 中的提交，格式为 `v0.1.0` 或 `0.1.0`，并与 `.csproj` 和 `everest.yaml` 中的 Mod 版本一致。Actions 页面也可手动运行构建，仅生成下载产物，不发布。

构建时自动按 `everest.yaml` 的依赖版本下载 Everest 官方 stable Release 的 `lib-stripped.zip` 和 CCT 官方对应 Release 的安装包。更新 CCT 时修改清单中的依赖版本即可；不自动追踪 latest。新版本 API 不兼容会导致编译失败，行为兼容性仍需游戏内验证。

发布新版：修改项目和清单中的 Mod 版本，提交到 main，再推送对应标签。ZIP 文件名和程序集版本自动跟随项目版本，发布包不包含下载的依赖 DLL。已存在的 Release 不会被覆盖。

### 阿里云 CDN 发布配置

标签发布会先上传 OSS，再创建含 CDN 下载链接的 GitHub Release；OSS 上传失败时不创建 Release。手动构建不上传 OSS。

在 GitHub 仓库 Settings → Secrets and variables → Actions 中配置两个 Secrets：

- `OSS_ACCESS_KEY_ID`：阿里云 AccessKeyId。
- `OSS_ACCESS_KEY_SECRET`：阿里云 AccessKeySecret。

凭证仅通过上传步骤的环境变量读取，不写入脚本或安装包。请使用专用 RAM 身份，并仅授予 `acs:oss:*:*:aliyun-static-diving-fish/cngist/*` 上的 `oss:PutObject` 和 `oss:GetObject` 权限（后者用于重试时 HEAD 校验），无需列桶或删除权限。

上传区域为 `cn-shanghai`，桶为 `aliyun-static-diving-fish`，对象路径为 `cngist/CNGoldenLink-<版本>.zip`。玩家下载地址为 `https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-<版本>.zip`。需在阿里云侧提前配置此域名的 CDN 回源与 HTTPS；私有桶需要 CDN 授权回源。脚本不修改桶 ACL 或 CDN 配置。

版本文件禁止覆盖，缓存时间为一年。若上传成功但 GitHub 发布失败，重试仅在已有对象的 SHA-256 元数据和大小与本地包一致时继续；内容不同需使用新版本。请限制发布标签和 workflow 的修改权限，能修改并运行发布代码的人员可能读取上传凭证。

本地也可在安全配置上述环境变量后运行（不要把真实密钥写进命令或提交到仓库）：

```powershell
python -m pip install -r scripts/requirements-upload.txt
python scripts/upload-oss.py artifacts/CNGoldenLink-0.1.0.zip
```

无需安装游戏的本地构建（需要 Python 3）：

```powershell
python scripts/prepare-ci.py
./scripts/package.ps1 -CelestePath ./artifacts/ci/references -CctAssemblyPath ./artifacts/ci/references/ConsistencyTracker.dll
```
