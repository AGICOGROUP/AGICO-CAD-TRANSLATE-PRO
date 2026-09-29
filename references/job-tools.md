# 共享原生任务工具

scripts/native_job.py 是新原生流程的确定性任务与验收工具。它不翻译、不调用外部模型、不运行历史流水线。原生 CAD 工具由当前模型按 native-dwg.md 在本次工作目录选择或构建；可用可靠的当前原生工具，但不能导入旧 PRO 的 manifest/JSONL/插件。

## 创建、提取、计划

```powershell
python scripts/native_job.py prepare --source "输入.dwg" --job "work/cad/新任务" --mode replace --source-language zh --target-language en
```

双语 mode 使用 bilingual。任务目录已存在会拒绝创建，测试必须重新复制源文件。记录 sourcePath/sourceSha256/mode/direction 后不改任务绑定。CAD 永远只打开 job/input.dwg 或新的候选核验副本。

原生提取输出 extracted.json，采用 data-contracts.md。模型完成语义重建、术语检索和翻译后写 mapping.json；单语 edits/reflows，双语 edits/additions。完整源记录都有动作，原文必须匹配。

```powershell
python scripts/native_job.py validate --job "work/cad/新任务"
```

写回前 validate 必须通过，并核对 input.dwg 当前 SHA-256 等于 mapping.cadInputSha256。字段内容、多内容 Table 和不可读对象不能静默忽略。共享校验器拒绝旧合约、遗漏记录、源文不符、双语修改原文、空目标、重复配对及伪造已有双语。

## 共享工具采用的精确扩展字段

保留原包 data-contracts.md；使用本项目共享校验器时补齐下列确定性字段，不能仅凭最小原包示例推断默认值。

- mapping 顶层必需 `mode`（replace/bilingual）、`sourceLanguage`、`targetLanguage` 与 `scope.sheetIds`。sheetIds 是本次原生提取、图框审查确认的实际图纸清单，视觉审查必须逐项覆盖同一清单。
- retain 必须写 retainReason；双语保留的 rows 必须实际提取 block/layer/color/style/font/bigFont/position/height/width/rotation；TEXT/ATTRIB/ATTDEF 另需 alignmentPoint、widthFactor。不得用缺失字段或双方 null 证明源外观未变。width 在 TEXT 中仍保留原包含义，widthFactor 单独记录。
- 双语 source/saved 顶层 `worldBoxCoverage: "complete"`。每条 row 的 `worldBoxes` 是从所有实际可见实例逐层组合完整变换后，原生包围盒所有角点投影得到的列表，每项 `{root: 实际模型或纸空间布局名, instancePath: [从根到该实体的引用handle], box: [minX,minY,maxX,maxY]}`。未引用块允许为空列表，但 blocks 中必须由真实引用图记录 layout=false、visibleInstanceCount=0；模型/纸空间行和所有可见块行不得为空，不能将可见块伪报为空。不同块的文字只要出现在同一 root，均做相交检查；纸空间视口按实际显示投影补齐。未知变换必须记 coverageIssues 并阻塞。
- reflows 每组必需 group、sourceHandles、block、properties、target、layoutPlan、protectedValues、verificationIssues；edit 的 action=reflow-member 同时指定 reflowGroup，sourceHandles 必须唯一确定一条文字记录。复杂 Table 多slot使用原生专用重排适配，不能靠不明确的handle归属。

最小重排结构（值由当前原生提取与实际区域决定）：

```json
{"edits":[{"handle":"A1","kind":"TEXT","slot":"text","expectedRaw":"基础","action":"reflow-member","reflowGroup":"note-1","target":"","layoutPlan":{},"protectedValues":[],"verificationIssues":[]}],"reflows":[{"group":"note-1","sourceHandles":["A1"],"block":"*Model_Space","properties":"A1","target":"Foundation","layoutPlan":{"position":[0,0,0],"width":12,"height":2,"region":[0,-5,12,0]},"protectedValues":[],"verificationIssues":[]}]}
```

重排收据以 group 绑定实际新 handle；保存后来源已移除，新目标内容/所属块/清单覆盖闭合。写回状态 schemaVersion=1，errors=[]，candidateSha256 必须等于已关闭候选的实际哈希。保存后快照 engine.inputPath 必须就是 saved/input.dwg，不能用任意路径宣称重新打开。

## 隐藏运行原生 CAD

```powershell
python scripts/native_job.py run-native --job "work/cad/新任务" --engine "实际AutoCAD目录/accoreconsole.exe" --script "work/cad/新任务/export.scr" --stage export
```

引擎/SDK 必须实际匹配；不写死 AutoCAD 版本。run-native 使用 CREATE_NO_WINDOW，日志重定向到工作目录，等待进程结束。原生命令必须在保存完成、文件流关闭后才写完成状态；工具等待退出，若卡在退出提示则只结束本任务进程树。脚本结尾应退出会话（不保存到源文件）；所有会话输入都已隔离。

工具通过环境传递 CAD_NATIVE_JOB、CAD_NATIVE_INPUT、CAD_NATIVE_STAGE、CAD_NATIVE_SOURCE_HASH、CAD_NATIVE_INPUT_HASH。原生命令分别输出 export-status.json、write-status.json、verify-status.json；失败 status 为 failed 并带 errors，不能继续交付。

export 的 extracted.json 采用共享契约；run-native 结束后记录输入副本当前哈希。write 原生事务只根据当前 mapping 写 candidate.dwg 到独立路径，保留原 DWG 版本。write-status.json 要包含保存文件 hash，新增目标的 additions/reflows 收据和全部处理数量。先 validate 再调用 stage write。

stage verify 从已关闭的 candidate.dwg 再复制 saved/input.dwg，新会话提取 saved/extracted.json；其中 engine.inputPath 必须记录实际核验副本路径。工具记录 openedCandidateSha256，不能手工补写这个字段绕过核验。

## 原生可视审查

逐张记录实际图号/图框，内部直接原生 PNG，不经 PDF/DXF 转换。文字密集处额外近景。scripts/native_render.py 可在隐藏 Core Console 中通过 --layout 指定提取清单中的真实模型或纸空间布局名，输出对应窗口；不能把本机“布局1”猜成 Layout1。脚本按 Windows 实际 ANSI 代码页写本地化参数，编码不支持时使用当前原生工具补齐，不忽略失败，原生 PNG 设备也可按图框/布局使用。

visual-review.json 包含：sourceSha256、candidateSha256、translationReviewed、symbolsReviewed、issues、warnings、sheets。每个 sheet 包含 id、reviewed、sourceImage、candidateImage、denseRegions；每个 denseRegion 也包含 sourceImage/candidateImage。两个角色的图片都必须是本任务的实际原生 PNG，并各自有 `.png.render.json`，记录 native: true、drawingSha256、pngSha256。图片存在不等于看过，reviewed 由模型实际审查后写入，不能根据残留零或退出码自动赋值。

视觉查看到的翻译、遮挡、漏字、字体/符号问题先修正；review.issues 非空不通过。warnings 仅记录已审查且不影响使用的轻微问题和原图矛盾。

```powershell
python scripts/native_job.py verify --job "work/cad/新任务"
python scripts/native_job.py deliver --job "work/cad/新任务" --output "outputs"
```

verify 从保存后 rows 对照全部源动作、新目标、几何、块变换和视觉绑定。仅在所有检查通过时覆盖写 verification.json 的 verified；deliver 再核对源/候选 hash，输出递增版本 DWG。默认只有最终 DWG 为附件。

每次修改 mapping/candidate 都应移除旧 verification.json 并重新保存后核验，不能复用之前的 verified。同一任务的已完成译文可用于修正；新测试不得读取历史译文。
