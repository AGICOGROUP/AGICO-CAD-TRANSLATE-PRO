# 双语扩展契约

共用 data-contracts.md 的 `schemaVersion: 1`、`tableIndexBase: 0`、`sourceSha256`、`cadInputSha256`、完整 rows 清单和稳定标识。顶层增加 mode、sourceLanguage、targetLanguage。双语 mode 为 bilingual，reflows 为空，所有 edits 均为 retain；基础原生提取格式保持不变。

## 源记录 edits

每条包含 handle、kind、slot、expectedRaw、action、target、needsTranslation、retainReason、layoutPlan、protectedValues、verificationIssues。target 与原 raw 相同。表格同时保留零基 row/column/contentIndex。

- `needsTranslation: true` 且本次添加：retainReason 为 covered-by-addition，sourceKeys 出现在唯一 addition。
- `needsTranslation: true` 且已有完整译文：retainReason 为 existing-bilingual，同时包含 counterpartKeys、counterpartComplete: true 和 counterpartMeaning。counterpartKeys 必须指向本次源清单里真实存在的非空目标文字；语义完整性由当前模型审查。
- 同一 MText / Table slot 内已有完整双语：needsTranslation=true，retainReason=existing-bilingual-inline，inlineSource / inlineTarget 是本次 visibleText 中真实存在、分别属于源/目标语言的完整片段，counterpartComplete=true，counterpartMeaning 说明完整语义；原 raw 全部保留，不新增重复译文。不得用一个英文缩写证明整段译完。
- 不需翻译：needsTranslation: false，注明 already-target、engineering-token 或 template-placeholder 等理由。中文或其他源语言不能仅因模板已有双语而归为 already-target。工程代码与专名保留时写 retainExplanation。

## additions

每个目标语义组包含：

| 字段 | 内容 |
| --- | --- |
| id | 本次唯一的语义配对 id |
| sourceKeys | 全部来源 `[handle, slot]`，不可同时归属于多个 addition |
| block | 与来源相同的非外参块/空间 |
| properties | 继承图层颜色的代表源实体 handle |
| target | 完整目标原生内容，无临时保护标记 |
| placement | position、rotation、width、height、region、minReadableHeight |
| protectedValues | 源/目标数值、型号、等级、符号等对应 |
| verificationIssues | 未解决问题；非空时阻止写回 |

position 为新增 MText 的 TopLeft 锚点、三维块局部坐标；region 为同一坐标系的 `[minX,minY,maxX,maxY]`。一般采用 MText，原对象仍是原对象。针对 Table 新增文字采用独立目标 MText 放入经确认的原单元格空白区域，sourceKeys 仍是原表格零基 slot。原网格不变。

原生写回成功状态 `write-status.json` 包含 status: written-awaiting-verification、candidatePath、candidateSha256、counts、errors、additions 与 reflows。每个 additions 收据记录 id、sourceKeys、handle、slot；保存后完整提取必须能读到该对象及完全相同的 target。不得用内存文字或手改候选哈希充当保存后证据。

源保留示例：

```json
{
  "handle": "A1", "kind": "TEXT", "slot": "text", "expectedRaw": "基础",
  "action": "retain", "target": "基础", "needsTranslation": true,
  "retainReason": "covered-by-addition", "layoutPlan": {},
  "protectedValues": [], "verificationIssues": []
}
```

配对新增示例（区域必须由实际图面确定）：

```json
{
  "id": "foundation-label", "sourceKeys": [["A1", "text"]],
  "block": "*Model_Space", "properties": "A1", "target": "Foundation",
  "placement": {
    "position": [0, -1, 0], "rotation": 0, "width": 12, "height": 2,
    "region": [0, -5, 12, -1], "minReadableHeight": 1.3
  },
  "protectedValues": [], "verificationIssues": []
}
```

## 保存后核验

原文 raw、归属、位置、旋转、字号、宽度因子、样式、字体、图层及颜色不变；原非文字几何与块引用变换不变。目标实际高度达到计划可读底限，实际包围盒在 region 内，与同块文字及投影到同一实际模型/纸空间根的全部实例文字无实质重叠；必需 worldBoxCoverage/worldBoxes 与外观字段见 job-tools.md。新文本数量必须完全由 additions 收据解释，没有未登记对象。

新增网格等非文字几何目前不属于共享工具自动通过的范围；如确有需要，先补齐明确的新增几何计划、专用写回和保存后收据验证，不能绕过几何检查。

共用核验只检查确定性关系，不能代替逐张语义/符号/视觉审查。验证状态只有 failed、written-awaiting-verification、verified，不使用旧 deliveryReady 或旧 replace/bilingual-v2 凭据。
