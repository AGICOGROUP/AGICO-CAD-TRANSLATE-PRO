# 提取、映射和状态契约

实施原生提取/写回程序时使用此最小契约；不是固定的示例图纸或旧译文缓存。中间文件均 UTF-8 JSON，放当前任务的工作目录。允许增加实际所需字段，提取器与写回器必须采用同一版本和索引约定。

## 提取清单

顶层字段：

| 字段 | 类型与含义 |
| --- | --- |
| `schemaVersion` | 整数 `1` |
| `sourceSha256` | 原始用户 DWG 的真实 SHA-256，64 位十六进制 |
| `cadInputSha256` | 当前原生输入副本的真实 SHA-256；在提取会话结束后记录 |
| `engine` | 实际使用的引擎、版本和原生输入路径 |
| `tableIndexBase` | 整数 `0`，row、column、contentIndex 全部零基 |
| `rows` | 本文件文字记录数组，包括待译与明确保留项 |
| `blocks`, `layouts`, `styles` | 引用关系/Origin、布局和实际字体信息 |
| `geometry` | 非文字实体的实际几何参数及属性快照 |
| `coverageIssues` | 外参、代理、图片、字段等未覆盖项数组；未知项不能伪报为空 |

每条 `rows` 的必要字段：

- `handle`：本数据库稳定实体标识，统一大写十六进制；不得使用显示顺序代替。
- `kind`：`TEXT`、`MTEXT`、`ATTDEF`、`ATTRIB`、`MLEADER`、`DIMENSION` 或 `TABLE`。其他含字对象新增显式类型及专用读写器，不能伪装成 TEXT。
- `slot`：普通文字为 `text`；表格为 `row,column`，多个内容项时为 `row,column,contentIndex`。表格另存相同的整数 `row`、`column`、可选 `contentIndex`，互相校验，全部零基。
- `raw`：原生 API 返回的完整原始串；`visibleText`：实际显示内容或经过控制串解码的翻译输入，说明其取得方式。
- `block` / `space`：所属块或空间。属性另记 `owner`、`tag`；多行属性及字段另存原生内容/表达式和显示值。
- `layer`、`style`、`font`、`bigFont`、`position`、`alignmentPoint`、`rotation`、`height`、`width`、`box`：实际提取值；长度采用当前图纸单位，角度弧度，位置/包围盒标明块局部坐标。
- `instances`：可见嵌套实例及完整变换，或指向单独实例清单。未知几何/显示信息记录为 `null` 和原因，不能补造位置、字体或碰撞通过结果。

字段文字与块属性多行内容须使用专用存储和专用写回器。表格一个单元格存在多个内容项时不能只读 `TextString` 后声称全部提取。

## 翻译映射

顶层保留 `schemaVersion`、`sourceSha256`、`cadInputSha256` 和 `tableIndexBase`，以及 `edits` / `reflows`。每条 edit 至少包含：

| 字段 | 约定 |
| --- | --- |
| `handle`, `kind`, `slot` | 与提取清单完全相同；表格同时校验数字索引 |
| `expectedRaw` | 与源 `raw` 完全相同，不能用解码后的文字代替 |
| `action` | `replace`（译文替换）、`retain`（注明保留原因）、`reflow-member`（并入明确重排组） |
| `target` | 最终原生内容串；`replace` 必须非空，格式/符号编码明确 |
| `layoutPlan` | 保留策略或具体排版参数、允许区域、坐标系、字体/字号和验证状态 |
| `protectedValues` | 工程数值、符号、编号及字段的源/目标对应，允许明确的表示形式转换 |
| `verificationIssues` | 未确认字体、符号、空间等问题；有未解决关键项时不可标为可交付 |

`reflow-member` 的空目标仅用于经过验证的合并重排；它必须引用存在的组，组中包含全部源实体、新 MText 内容、所属块、属性来源实体及布局参数。来源覆盖用组映射核对，不能将普通漏译或空译文伪装为合并。

写回前检查当前输入指纹及所有 `expectedRaw`。当前输入已变化则重新核实/提取，不复用旧映射。处理新会话自动保存造成的副本变化时，以当前提取完成后记录的输入指纹为准；始终单独保留原始用户源文件指纹。

来源缺少 `schemaVersion` 或索引约定时，先由实际提取器确认契约或重新原生提取。该缺口不能通过“通常是零基”猜测解决。

## 原生写回状态

每次运行生成独立状态 JSON：`schemaVersion`、`jobId`、`command`、`inputPath`、`inputSha256`、`candidatePath`、`status`、`counts`、`errors`。`status` 取 `failed`、`written-awaiting-verification`、`verified`；文件存在或 CAD 进程返回 0 不能升级状态。

`counts` 至少记录待处理、替换、保留、重排来源、成功和失败数；`errors` 记录实体定位、错误类别及原因，如 `source_mismatch`、`unsupported_entity`、`symbol_unverified`、`missing_dependency`。任何写回失败回滚事务，不保留部分成功候选作为完整交付。

`written-awaiting-verification` 只表示事务完成且候选保存成功。CAD 进程退出后，核验会话打开候选的新副本，核对来源覆盖、字体/字段/符号、图纸范围、实际工程几何、逐图视觉结果及原始源 SHA-256；这些都通过后才记录 `verified`。部分检查没有条件完成时明确写 `null` 或未通过原因，不能填假成功。
