# VeloxClip Windows 移植 — P1:剪贴板核心(设计文档)

- 文档日期:2026-05-18
- 子项目:Windows 移植 P1(P0–P7 中的第二个)
- 状态:设计已批准,待写实施计划
- 前置:P0 工程脚手架已合并到 `main`(PR #1,commit `9a175cf`)

## 1. 背景与上下文

VeloxClip 是一款 macOS 剪贴板管理工具,正在 1:1 功能复刻到 Windows 11。P0 已交付可构建、可运行、可发布的 .NET 8 + WinUI 3 脚手架(Core / Platform / App 三层、DI + Serilog + 单实例 + 托盘、GitHub Actions CI)。

P1 在 P0 之上实现**剪贴板核心**:后台监听剪贴板、捕获多种内容类型、持久化到 SQLite。P1 **没有用户可见 UI** —— 占位窗口仍是 "coming soon",可见的历史列表是 P2 的范围。P1 的产物是"后台默默把剪贴板历史写进本地数据库"。

### 1.1 P1 的总体技术决策(brainstorming 锁定)

| 决策点 | 选择 | 理由 |
|---|---|---|
| 剪贴板变更检测 | Win32 `AddClipboardFormatListener` + message-only 窗口 | WinRT `Clipboard.ContentChanged` 在 unpackaged + 后台运行时会漏事件;Win32 listener 后台可靠 |
| 消息泵位置 | 复用 WinUI UI 线程消息泵 | `WM_CLIPBOARDUPDATE` 极低频;UI 线程只"收消息→丢后台",开销可忽略 |
| 密码管理器黑名单 | P1 内置硬编码默认黑名单(无 UI) | 隐私违反一次即失信;硬编码列表 < 20 行;自定义 UI 留 P7 |
| 图片二进制存储 | 外置文件 `blobs/<id>.png` + DB 存相对路径 | `.db` 始终小而快,大图不拖慢"列最近 100 条"热查询 |
| 图片尺寸上限 | PNG 编码后 > 16 MB → 整条丢弃 | 防截图工具把 DB 撑爆;超大图记录意义小 |
| 剪贴板读取 API | 纯 Win32 `OpenClipboard`/`GetClipboardData` | 与 Win32 listener 一致;最可靠;不受 WinRT 格式怪癖影响 |
| SQLite 库 | `Microsoft.Data.Sqlite` | 微软官方、自带 SQLite、轻量 ADO.NET 无 ORM;net8.0 跨平台,可在 macOS 上单测 |
| 模式版本管理 | `PRAGMA user_version` | 比 Mac 的 `PRAGMA table_info` 差异比对干净 |
| 去重语义 | 1:1 复刻 Mac 两级行为 | 见 §6 |

## 2. 范围与非目标

### 2.1 范围(P1 必做)

- 后台监听剪贴板变化(Win32 listener)
- 捕获并分类五种内容:`text` / `rtf` / `image` / `file` / `color`
- 来源应用追踪(前台进程名)
- 两级去重(5 秒窗口 + 内容匹配移顶)
- SQLite 持久化(`clipboard_entries` + `app_settings` 两表)
- 图片外置 blob 存储 + 16 MB 上限
- 历史上限(默认 100,可经 `app_settings` 配置)
- 硬编码密码管理器黑名单
- 启动时孤儿 blob 对账

### 2.2 非目标(P1 不做)

- 任何用户可见 UI(历史列表是 P2)
- 全局热键、悬浮窗、粘贴回前台(P2)
- 收藏体系、自定义标签、内容类型自动打标(json/url/code/markdown 等)(P3)
- 关键字 / 语义搜索(P3 / v2)
- 内容预览组件(P4)
- OCR、LLM(P5)
- 截图、图片编辑器(P6)
- 设置面板、黑名单自定义 UI(P7)
- DB 损坏恢复(留 P7)

## 3. 架构

延续 P0 的分层契约:`App → Platform → Core`,依赖单向。`Core` 零 Windows 依赖,在 macOS/Linux 上可完整单测;`Platform` 集中 Win32 互操作。

### 3.1 `VeloxClip.Core`(纯逻辑,全部可单测)

| 文件 | 职责 |
|---|---|
| `Models/ClipboardEntry.cs` | 数据模型(见 §5.1) |
| `Models/ClipboardKind.cs` | `enum ClipboardKind { Text, Rtf, Image, File, Color }` |
| `Models/ClipboardCapture.cs` | `IClipboardReader` 的返回 DTO:`Kind` + `Text`(string?)+ `ImageBytes`(byte[]?) |
| `Abstractions/IClipboardChangeSource.cs` | 剪贴板变化事件源;`event EventHandler ClipboardChanged` |
| `Abstractions/IClipboardReader.cs` | `ClipboardCapture? TryRead()` —— 读当前剪贴板 |
| `Abstractions/IForegroundAppProvider.cs` | `string? GetForegroundProcessName()` |
| `Abstractions/IClipboardStore.cs` | 持久化接口:`Add`、`GetRecent(n)`、`GetMostRecent()` |
| `Abstractions/IBlobStore.cs` | `string Save(byte[])`(返回相对路径)、`Delete(path)`、`ReconcileOrphans(知道的路径集合)` |
| `Abstractions/IAppSettingsStore.cs` | `int GetHistoryLimit()`、`SetHistoryLimit(int)` |
| `Capture/ClipboardCaptureService.cs` | 核心编排器(见 §6) |
| `Capture/ColorDetector.cs` | hex/rgb/rgba 正则判定(复刻 Mac `isColor`) |
| `Capture/ClipboardDeduplicator.cs` | 两级去重判定 |
| `Capture/Blacklist.cs` | 硬编码进程名集合 + `ShouldIgnore(string?)` |
| `Persistence/SqliteClipboardStore.cs` | `IClipboardStore` 实现(Microsoft.Data.Sqlite) |
| `Persistence/FileBlobStore.cs` | `IBlobStore` 实现(`System.IO`,图片 PNG 写到 `blobs/`) |
| `Persistence/SqliteAppSettingsStore.cs` | `IAppSettingsStore` 实现(`app_settings` 表) |
| `Persistence/ClipboardSchema.cs` | 建表 DDL + `PRAGMA user_version` 迁移 |

### 3.2 `VeloxClip.Platform`(Windows-only,Win32 互操作)

| 文件 | 职责 |
|---|---|
| `Clipboard/Win32ClipboardChangeSource.cs` | message-only 窗口 + `AddClipboardFormatListener` + WndProc;收 `WM_CLIPBOARDUPDATE` 触发 `ClipboardChanged` |
| `Clipboard/Win32ClipboardReader.cs` | `OpenClipboard`/`GetClipboardData` 读 text/rtf/image/files;DIB→PNG 归一化 |
| `Clipboard/Win32ForegroundAppProvider.cs` | `GetForegroundWindow` → `GetWindowThreadProcessId` → 进程名 |
| `ClipboardMonitorHostedService.cs` | `IHostedService`:`StartAsync` 装 listener + 启动捕获服务,`StopAsync` 拆 |
| `PlatformServiceCollectionExtensions.cs` | 把上述全部注册进 DI(填充 P0 留的空 stub) |

### 3.3 对 P0 文件的定向改动

- `VeloxClip.Core/Environment/IAppPaths.cs` + `AppPaths.cs`:新增 `Blobs` 属性,指向 `%LOCALAPPDATA%\VeloxClip\blobs\`。
- `VeloxClip.Core/Environment/AppEnvironmentBootstrapper.cs`:`Ensure` 顺手创建 `blobs/` 目录。
- `AppPathsTests` / `AppEnvironmentBootstrapperTests`:补充对 `Blobs` 的断言。

这是 P1 唯一动到 P0 代码的地方,属于"为当前工作服务的定向改进"。

### 3.4 设计单元的边界

- `ClipboardCaptureService` 只依赖 Core 中的接口(`IClipboardReader` / `IClipboardChangeSource` / `IForegroundAppProvider` / `IClipboardStore` / `IBlobStore` / `IAppSettingsStore`)+ 纯逻辑类(`ColorDetector` / `ClipboardDeduplicator` / `Blacklist`)。因此用 fake 实现即可在 macOS 上全测整条流水线。
- Win32 互操作类保持极薄:每个类只做"P/Invoke + 把结果转成 Core 的 DTO",不含业务逻辑。逻辑都在 Core,以保证可测性。

## 4. 运行模型

- `ClipboardMonitorHostedService` 是一个 `IHostedService`,在 `PlatformServiceCollectionExtensions.AddVeloxClipPlatform` 中注册。P0 的后续修复已让 `App.OnLaunched` 调用 `_host.Start()`,因此该服务会随应用启动。
- `StartAsync`:① 启动时执行孤儿 blob 对账;② 创建 Win32 message-only 窗口并 `AddClipboardFormatListener`(在 UI 线程,因为消息泵在那里);③ 把 `IClipboardChangeSource.ClipboardChanged` 接到 `ClipboardCaptureService`。
- `StopAsync`:`RemoveClipboardFormatListener` + 销毁 message-only 窗口。
- 收到 `WM_CLIPBOARDUPDATE` 后,UI 线程立即 `Task.Run` 把整条捕获流水线 offload 到后台线程;UI 线程只承担"收消息→派发"。

## 5. 数据模型与持久化

### 5.1 `ClipboardEntry`

```csharp
public sealed record ClipboardEntry(
    Guid           Id,
    DateTimeOffset CreatedAt,
    ClipboardKind  Kind,         // Text | Rtf | Image | File | Color
    string?        Content,      // text/rtf/file/color 的负载;image 为 null
    string?        BlobPath,     // image 的相对路径 blobs/<id>.png;其它为 null
    string         ContentHash, // 负载的 SHA-256 十六进制(去重用)
    string?        SourceApp);   // 前台进程名;未知为 null
```

各 `Kind` 的负载约定:

- **Text / Color** → `Content` 存字符串(Color 即 `#FF5733` 这类原始串)
- **File** → `Content` 存换行拼接的文件路径列表(复刻 Mac)
- **Rtf** → `Content` 存 RTF 标记文本。RTF 本质是 7-bit ASCII,可安全进 TEXT 列,不需要 BLOB 列
- **Image** → `Content` 为 null,`BlobPath` 指向外置 PNG 文件

### 5.2 SQLite 模式(`PRAGMA user_version = 1`)

```sql
CREATE TABLE clipboard_entries (
    id            TEXT    PRIMARY KEY NOT NULL,  -- GUID
    created_at    INTEGER NOT NULL,              -- Unix epoch 毫秒 (UTC)
    kind          TEXT    NOT NULL,              -- 'text'|'rtf'|'image'|'file'|'color'
    content       TEXT,                          -- 见 §5.1;image 为 NULL
    blob_path     TEXT,                          -- image 的相对路径;其它 NULL
    content_hash  TEXT    NOT NULL,              -- 负载 SHA-256 hex
    source_app    TEXT                           -- 前台进程名;NULL = 未知
);
CREATE INDEX ix_entries_created_at ON clipboard_entries (created_at DESC);
CREATE INDEX ix_entries_hash       ON clipboard_entries (content_hash);

CREATE TABLE app_settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
```

DB 文件:`%LOCALAPPDATA%\VeloxClip\db\veloxclip.db`(P0 的 `AppPaths.Database` 目录)。
Blob 目录:`%LOCALAPPDATA%\VeloxClip\blobs\`(P1 新增的 `AppPaths.Blobs`)。

### 5.3 对 Mac 模式的两处刻意改进

1. **`content_hash` 列 + 索引**:Mac 去重靠扫描内存数组逐字段比对 `content`/`data`。本设计在捕获时算一次 SHA-256 存入该列,两级去重都变成 O(1) 索引查找,图片也不必把字节留在内存逐字节比。
2. **`created_at` 用 INTEGER 毫秒** 而非 Mac 的 double 秒:排序无浮点误差。这是全新数据库,不存在迁移负担。

`kind` 在 DB 中以小写字符串存储(`text`/`rtf`/`image`/`file`/`color`),与 `ClipboardKind` 枚举双向映射;用字符串而非整数,便于人工查库与未来扩展新类型。

## 6. 捕获流水线与去重

### 6.1 数据流

```
WM_CLIPBOARDUPDATE  (UI 线程,低频)
  └─ Win32ClipboardChangeSource 触发 ClipboardChanged 事件
       └─ ClipboardMonitorHostedService → Task.Run(...)  ── 以下全在后台线程 ──
            1. IForegroundAppProvider.GetForegroundProcessName()
            2. Blacklist.ShouldIgnore(进程名)?  → 是:中止
            3. IClipboardReader.TryRead() → ClipboardCapture?  → null:中止
            4. 分类:若 Kind=Text 且 ColorDetector 命中 → 改判 Kind=Color
            5. image:PNG 编码后字节数 > 16 MB → 整条丢弃,中止
            6. 计算 ContentHash:text/rtf/file/color 用 Content 的 UTF-8 字节;
               image 用 PNG 字节;均取 SHA-256 十六进制
            7. 去重 Tier-1(5 秒窗口):IClipboardStore.GetMostRecent() 若同
               ContentHash 且其 CreatedAt 距今 < 5 秒 → 丢弃,中止
            8. image:IBlobStore.Save(pngBytes) → 相对路径
            9. 组装 ClipboardEntry(新 Guid、CreatedAt=now)
           10. IClipboardStore.Add(entry):
                 ├─ 去重 Tier-2:按 content_hash 查到已存在行 → 把那行 created_at
                 │   刷成现在(移到顶端),不新插入;若本次 Kind=Image,
                 │   则删掉第 8 步刚写的新 blob 文件(避免孤儿)
                 └─ 否则插入新行 → 执行历史上限裁剪(见 §7)
```

整条流水线最外层包 try/catch:任何单次捕获异常只记一行日志,绝不让 listener 失效。

### 6.2 两级去重(1:1 复刻 Mac 语义)

- **Tier-1 / 5 秒窗口**(`ClipboardCaptureService` 内,第 7 步):防"快速连按复制"。同一内容 5 秒内重复出现 → 早早丢弃,省掉 blob 写入与 DB 往返。判定:最近一条的 `ContentHash` 相同且 `CreatedAt` 距今严格小于 5 秒。
  - 注:Mac 的 Tier-1 扫描"最近 10 条";本设计只查最近一条。因为 Tier-2 的移顶是兜底,即便 Tier-1 漏掉非相邻的重复,最终 DB 状态仍等价(不会产生重复行),差别仅在于偶尔多做一次无用的 PNG 编码 / 哈希计算。这是有意简化。
- **Tier-2 / 移到顶端**(`SqliteClipboardStore.Add` 内,第 10 步):用户隔很久再复制同样的旧内容 → 不产生重复行,而是按 `content_hash` 找到旧行,把其 `created_at` 刷新到当前时间(顶到列表最前)。

Mac 用 `5.0` 秒边界,本设计同。`ClipboardDeduplicator` 把"是否落在 5 秒窗口内"做成纯函数,便于测边界(4.9s 命中 / 5.1s 不命中)。

### 6.3 颜色识别

`ColorDetector` 复刻 Mac `isColor`:对去除首尾空白的文本匹配两个正则 ——
- hex:`^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3}|[A-Fa-f0-9]{8})$`
- rgb/rgba:`^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$`

任一命中则把 `Kind` 从 `Text` 改判为 `Color`。

### 6.4 图片归一化

`Win32ClipboardReader` 读图片时按优先级:
1. 剪贴板若存在已注册的 `PNG` 格式 → 直接用其字节(已是 PNG,无需转码)
2. 否则若有 `CF_DIBV5` / `CF_DIB` → 解析为位图后编码为 PNG
3. 否则若有 `CF_BITMAP`(HBITMAP)→ 包装为位图后编码为 PNG

最终统一交给 Core 的是 PNG 字节。具体用哪种 Windows 成像 API(`System.Drawing` / WinRT `BitmapEncoder`)在实施计划中确定;该取舍仅影响 `Win32ClipboardReader` 内部,不影响 Core 接口。

## 7. 历史上限

`app_settings` 表中键 `history_limit`,默认值 **100**(首次访问时若不存在则写入默认值)。

`SqliteClipboardStore.Add` 插入新行后:`SELECT COUNT(*) FROM clipboard_entries`,若超过 `history_limit`,按 `created_at ASC` 删除最旧的多余行;每删一条 `kind='image'` 的行,同时调用 `IBlobStore.Delete` 删其 blob 文件。

P1 没有收藏概念,上限统计全部条目。P3 引入收藏后会改为"只统计非收藏项",届时为独立改动。

## 8. 黑名单

`Blacklist.cs` 持有一个大小写不敏感的 `HashSet<string>`,内容为进程名(不含 `.exe` 后缀):

```
1password, bitwarden, keepass, keepassxc, lastpass, dashlane, enpass, roboform
```

`ShouldIgnore(string? processName)`:把传入进程名去掉 `.exe` 后缀(若有)、转小写后查集合;命中返回 `true`。`processName` 为 null 时返回 `false`(未知来源不屏蔽,以免漏记正常复制)。命中则该次捕获在流水线第 2 步直接中止,不读不写。

Mac 用 bundle ID;Windows 无直接对等品,以进程可执行名替代 —— P1 捕获 `SourceApp` 时本就取进程名,顺手比对。

## 9. 错误处理

| 场景 | 处理 |
|---|---|
| 剪贴板被别的进程占用(`OpenClipboard` 失败) | 短退避重试至多 5 次(每次约 20ms);仍失败 → 记 warning,跳过本次事件 |
| 未知 / 空剪贴板格式 | `TryRead` 返回 null → 静默跳过(非错误) |
| image PNG 编码失败 / DIB 解析失败 | 记 warning,该条按"不支持"丢弃,不中断 monitor |
| blob 文件写入失败(磁盘满等) | 记 error,**整条丢弃** —— 绝不插入指向不存在文件的行 |
| SQLite 写入失败 | 记 error;本次条目丢失,应用继续运行 |
| 孤儿 blob(行删了文件没删,或文件在但 DB 无引用) | 启动时双向对账(见下) |
| DB 损坏 / 迁移失败 | 超出 P1 范围,记 error,留 P7 |

**铁律:** 捕获流水线最外层 try/catch 兜底 —— 任何单次捕获异常都不能让 `WM_CLIPBOARDUPDATE` listener 失效。一次失败 = 一行日志,下次复制照常工作。

### 9.1 启动时双向孤儿对账

由 `ClipboardMonitorHostedService.StartAsync` 在装 listener 之前编排,同时用到 `IClipboardStore` 与 `IBlobStore`:

1. 从 `IClipboardStore` 取出全部 `kind='image'` 行的 `blob_path` 集合(称"已引用路径")。
2. **文件→无行**:`IBlobStore.ReconcileOrphans(已引用路径)` 列出 `blobs/` 目录下所有文件,删掉不在该集合中的(野文件)。
3. **行→无文件**:对每个已引用路径检查文件是否存在;不存在 → 该 image 行为孤儿 → 通过 `IClipboardStore` 按 id 删除该行。

为支持第 1、3 步,`IClipboardStore` 需提供"枚举全部 image 行的 (id, blob_path)" 与"按 id 删除"两个能力(精确方法签名在实施计划中定)。

## 10. 测试策略

### 10.1 Core 层 —— macOS/Linux 上即可全跑

`Microsoft.Data.Sqlite` 在 net8.0 上跨平台,SQLite 相关测试用临时文件库即可在 mac 上跑。

| 测试类 | 覆盖 |
|---|---|
| `ColorDetectorTests` | hex(3/6/8 位)、rgb、rgba 正例;非颜色文本负例;首尾空白容忍 |
| `ClipboardDeduplicatorTests` | 5 秒窗口边界(4.9s 命中 / 5.1s 不命中)、hash 相等判定、Tier-2 移顶决策 |
| `BlacklistTests` | 已知进程名屏蔽、大小写不敏感、`.exe` 后缀剥离、未知进程放行、null 安全 |
| `FileBlobStoreTests` | 保存返回相对路径、读回字节一致、删除、孤儿对账(临时目录) |
| `ClipboardSchemaTests` | 全新库建表 + 索引 + `user_version=1` |
| `SqliteClipboardStoreTests` | 插入、按时间倒序查最近 N、`content_hash` 移顶、上限裁剪 + 同步删 blob(临时文件库) |
| `SqliteAppSettingsStoreTests` | `history_limit` 读 / 写 / 默认 100 |
| `ClipboardCaptureServiceTests` | 用 fake reader/changeSource/foregroundApp/blobStore 跑全流水线:黑名单短路、image 16 MB 上限丢弃、Tier-1 去重、color 改判、正常落库 |

### 10.2 Platform 层

Win32 互操作无法在 macOS 单测,靠:CI 在 `windows-2022` 上构建通过 + P1 Windows 真机烟雾测试(见 §11)。Win32 包装类保持极薄,逻辑都在 Core。

## 11. P1 验收标准(Definition of Done)

- [ ] `VeloxClip.Core.Tests` 全绿,新增 8 个测试类全部通过(macOS 本地 + CI)
- [ ] CI `build-windows.yml` 在 `windows-2022` 上构建 + 测试通过,0 warning
- [ ] Windows 真机:复制文本 / RTF(从 Word)/ 图片(截图)/ 文件(资源管理器)/ 颜色串(`#FF5733`)各一次,`veloxclip.db` 出现 5 行,`kind` / `content` / `blob_path` / `source_app` 均正确
- [ ] 图片行的 `blob_path` 指向 `blobs/` 下真实存在的 PNG 文件
- [ ] 3 秒内连续复制同一文本两次 → 只有 1 行(Tier-1 生效)
- [ ] 复制 A、复制 B、再复制 A → A 行 `created_at` 被刷新顶到最前,共 2 行而非 3 行(Tier-2 生效)
- [ ] 把 `history_limit` 设为 5,复制 7 次不同内容 → DB 只剩 5 行,最旧 2 行连同 blob 被删
- [ ] 从密码管理器(如已装 Bitwarden)复制 → 不产生任何行
- [ ] 剪贴板被占用 / 复制不支持的格式 → 日志有记录,monitor 不死,后续复制正常
- [ ] 启动时孤儿 blob 对账生效:手动在 `blobs/` 放一个野文件,重启后被清掉

## 12. 后续阶段一览(P1 上下文,不在本 spec 范围)

| 阶段 | 子项目 |
|---|---|
| P2 | 主界面 + 全局热键:Spotlight 风格悬浮窗、列表视图、键盘导航、粘贴回前一应用 |
| P3 | 搜索 + 收藏 + 标签:关键字搜索、收藏体系、内容类型自动打标、自定义标签 + 颜色 |
| P4 | 内容预览组件 |
| P5 | OCR + LLM |
| P6 | 区域截图 + 图片编辑器 |
| P7 | 设置面板 + 黑名单自定义 UI + 收尾 |
| v2(后置) | 语义搜索 |
