# 启动器自有模组市场列表

`market-ext.json` 会在启动器拉取 ModHub 索引后按字段级合并（`id` → `repo + 产品线` → 校验资产名 → `bootNames/aliases` → 名称）。

## 提交约定

- 只改本目录数据时，提交信息用 `data:` 前缀，例如 `data: 新增 xxx 条目`。
  这类提交不会触发启动器构建（`build.yml` 已忽略 `market/**`）。
- 改动代码请照常使用 `feat:` / `fix:` 等前缀。
- 提交前本地可运行：
  ```bash
  npx ajv-cli@5 validate -s market/market-ext.schema.json -d market/market-ext.json --spec=draft2020
  ```

## 字段说明

只写需要覆盖的字段，未出现的字段保持上游（ModHub）值：

- `id`：对应 ModHub 条目 id，最优先的匹配键。
- `repo`：`owner/repo`，新增条目必填；同仓库多产品线时配合 `bootNames` / `editions` 区分。
- `name` / `description` / `tags` / `category` / `author`：覆盖展示信息。
- `verifiedReleaseAsset.fileName`：指定优先下载的资产名。
- `editions`：包型列表（`id` / `label` / `bootName` / `assetSeries` / `verifiedAssets`）。
- `hidden: true` 或顶层 `withdrawnIds`：下架条目。
