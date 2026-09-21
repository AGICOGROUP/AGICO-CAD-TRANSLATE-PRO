# AutoCAD 诊断命令崩溃排查与修复

日期：2026-09-17。目录：`D:\AGICO-CAD-TRANSLATE-PRO`。

## 已确认的触发链

截图文件创建于 13:45:48。与其时间相邻的最新 Autodesk CER 记录为 13:44:18，进程 24548，命令 `CAD_BILINGUAL_REVIEW_TEST`。`senddmp.log` 中的未处理异常为：

```text
System.ArgumentNullException: Value cannot be null. (Parameter 'path1')
at System.IO.Path.Combine(String path1, String path2)
at ContactTests.ReviewExisting()
at Autodesk.AutoCAD.Runtime.CommandClass.CommandThunk.Invoke()
```

启动脚本允许指定复核命令，却未检查它必需的 `CAD_LAYOUT_REVIEW_JOB`。复核入口直接把空值传给 `Path.Combine`，且没有命令级异常边界，普通参数错误最终进入 AutoCAD 的异常报告流程。原生回归已在修复前复现相同调用栈；外层测试捕获异常，避免为复现再制造一次宿主崩溃。

截图里的 `0xc0000005` 本身不能定位业务代码。上述修复针对已确认的异常外泄路径；不据此声称所有同码崩溃都已解决。另一个导入任务退出码为 `4294967295`，未找到对应 CER 记录，不能与此次测试命令崩溃合并归因。缺字体提示也没有证据表明是此次原因。

## 修复

- `run_near_label.py` 增加 `--job`，复核和探查命令在启动 CAD 前检查任务目录、必需文件及插件。缺参直接报出原因。
- `ContactTests.ReviewExisting` 使用 `NativeTestCommand` 命令边界。缺参、坏配置和文件错误输出 `status=failed`；报告路径本身失效时退回控制台，不再次把异常抛给宿主。
- 原生报告先写临时文件，再替换正式报告。启动器等待完整 JSON，宿主无报告退出或超时生成结构化失败，不再二次报“找不到 report.json”。
- 正常复核仍保留 `risks` 与原有诊断范围说明。`status=passed` 仅表示复核命令执行成功，不代表图纸无风险或达到交付标准。

未关闭 Autodesk 错误报告、修改系统注册表或降低翻译质量检查要求。本次只修改诊断启动与异常处理，不改翻译排版策略。

## 验证

- 新增 Python 回归 4 项通过：缺参提前拒绝、缺文件提前拒绝、无报告退出、正确传递任务目录并保留风险结果。
- 真实 AutoCAD 原生回归 6 项通过：缺任务目录、缺文件、坏配置、报告不可写、缺报告路径，以及正常复核保留真实重叠风险。
- 正常路径使用本次新建的双文字 DWG 和配套记录，未复用历史翻译结果。独立运行复核命令仍报告 `saved-text-overlap`。
- 在真实命令入口直接输入坏配置，得到失败 JSON 和退出码 1，没有异常逃出命令；没有新增 CER 记录。验证后 `senddmp.log` 最后修改时间仍为 13:44:18。
- 全量 Python 回归：127 项中 126 项通过。唯一失败是已有的术语表固定计数断言：HEAD 中仍断言 1293 条，修正版文档为 1252 个词条；与此次修改无关，未将其改为通过以掩盖差异。
- 原生测试程序集构建成功，0 错误；保留既有的 Autodesk 引用版本与可空性警告。

验证文件位于 `tmp/crash-boundary-red`、`tmp/crash-boundary-fixed`、`tmp/crash-review-end-to-end`、`tmp/crash-review-invalid-config` 和 `tmp/crash-python-regression.log`。

当前另一个任务正在同一目录的 `glm` 分支修改排版代码，因此未切换分支、未新建分支，也未替它提交未完成的改动。
