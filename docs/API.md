# Caishenfolio Analytics Core API（P3）

- 默认地址：`http://127.0.0.1:8765`
- 协议：HTTP + JSON（`Content-Type: application/json; charset=utf-8`）
- 绑定：仅 loopback（`127.0.0.1` / `::1`），禁止 `0.0.0.0`
- 权威：C# Host 启动/鉴权；Python Core 提供下列路由

启动：

```powershell
$env:PYTHONPATH = "$PWD\python"
$env:CAISHENFOLIO_MARKET_PROVIDER = "akshare"   # 或 fixture
python -m caishenfolio_core.server --host 127.0.0.1 --port 8765
```

---

## 一览

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/health` | 健康检查、阶段、行情源状态 |
| GET | `/market/diagnostics` | 行情诊断与使用提示 |
| GET | `/symbols/search` | 标的搜索 |
| GET | `/market/bars` | 历史 K 线（OHLCV） |
| GET | `/tasks` | 任务列表 |
| POST | `/tasks` | 创建任务 |
| GET | `/tasks/{id}` | 任务详情 |
| GET | `/tasks/{id}/audit` | 任务审计事件 |
| GET | `/tasks/{id}/artifacts` | 任务产物列表 |
| POST | `/research/symbol-snapshot` | 研究快照（Task+Artifact+Audit） |
| POST | `/research/backtest-ma` | MA 交叉简单回测（模拟） |
| POST | `/research/compare` | 多标的归一化收盘价对比 |
| POST | `/research/export-report` | 导出 Markdown/HTML 报告到 Artifact 根 |
| POST | `/market/export-parquet` | 导出 K 线为 Parquet（无 pyarrow 则 JSONL） |

---

## GET `/health`

**用途：** 核心是否存活、当前阶段、行情源是否可用。

**响应示例：**

```json
{
  "status": "ok",
  "product": "Caishenfolio",
  "version": "0.4.1",
  "phase": "P3",
  "disclaimer": "研究/模拟结论，非投资建议。",
  "live_trading_enabled": false,
  "market_provider": "akshare",
  "market_provider_ready": true,
  "market_data_synthetic": false,
  "http_trust_env": true
}
```

| 字段 | 含义 |
| --- | --- |
| `market_provider` | `akshare`（真实）或 `fixture`（合成演示） |
| `market_provider_ready` | 依赖是否可用（如 akshare 是否安装） |
| `market_data_synthetic` | 是否为合成数据 |
| `http_trust_env` | 是否遵循系统代理环境变量 |
| `live_trading_enabled` | 恒为 `false`（不做实盘） |

---

## GET `/market/diagnostics`

**用途：** 本地诊断，不拉全市场数据。

**响应字段：** 同 health 行情字段 + `tips[]` + `supported_examples[]`。

---

## GET `/symbols/search`

**查询参数：**

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `q` | 否 | 代码/名称关键字；可传 `SSE:600000` |
| `limit` | 否 | 默认 10，最大 50 |

**示例：** `GET /symbols/search?q=600000&limit=10`

**响应：**

```json
{
  "provider": "akshare",
  "items": [
    {
      "symbol": "SSE:600000",
      "market": "ashare",
      "asset_class": "equity",
      "name": "浦发银行",
      "provider": "akshare"
    }
  ]
}
```

内部标的格式：`EXCHANGE:CODE`（如 `SSE:600000`、`HKEX:00700`、`NASDAQ:AAPL`、`FUND:000001`）。

---

## GET `/market/bars`

**查询参数：**

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `symbol` | 是 | 如 `SSE:600000` |
| `start` | 是 | `YYYY-MM-DD` |
| `end` | 是 | `YYYY-MM-DD` |
| `adjustment` | 否 | `raw` / `forward` / `backward` / `unknown` |

**示例：**  
`GET /market/bars?symbol=SSE:600000&start=2024-01-02&end=2024-01-12&adjustment=raw`

**成功响应：**

```json
{
  "ok": true,
  "provider": "akshare",
  "data": [
    {
      "timestamp_utc": "2024-01-12T00:00:00+00:00",
      "open": 6.52,
      "high": 6.56,
      "low": 6.5,
      "close": 6.5,
      "volume": 34624053.0,
      "amount": null,
      "currency": "CNY",
      "adjustment": "raw",
      "provider": "akshare",
      "provenance": {
        "source": "akshare",
        "symbol": "SSE:600000",
        "source_api": "stock_zh_a_hist",
        "synthetic": "false"
      }
    }
  ],
  "warnings": ["real_market_data", "not_for_investment_decisions"],
  "error": null
}
```

**失败（fail-closed，不造数）：**

```json
{
  "ok": false,
  "provider": "akshare",
  "data": null,
  "warnings": ["fail_closed", "..."],
  "error": "……错误说明……"
}
```

---

## 任务 Task / Artifact / Audit

### GET `/tasks`

| 参数 | 说明 |
| --- | --- |
| `kind` | 可选：`system` / `market_data` / `research` / `simulation` / `report` |
| `status` | 可选：`created` / `running` / `waiting_for_user` / `succeeded` / `failed` / `cancelled` |
| `limit` | 默认 50 |

### POST `/tasks`

```json
{
  "kind": "research",
  "title": "示例任务",
  "metadata": { "symbol": "SSE:600000" }
}
```

### GET `/tasks/{id}`

返回单个任务对象。

### GET `/tasks/{id}/audit`

| 参数 | 说明 |
| --- | --- |
| `event_type` | 可选过滤 |
| `limit` | 默认 50 |

### GET `/tasks/{id}/artifacts`

返回该任务关联产物列表。

**说明：** Core 进程内任务为内存存储；Desktop 会把研究结果镜像到本机  
`%LocalAppData%\Caishenfolio\state\tasks.db`。

---

## POST `/research/symbol-snapshot`

**用途：** 首个研究命令。拉行情 → 写 Task/Artifact/Audit → 返回摘要。

**请求体：**

```json
{
  "symbol": "SSE:600000",
  "start": "2024-01-02",
  "end": "2024-01-12",
  "adjustment": "raw"
}
```

**成功：** HTTP 200，`ok: true`，含 `task` / `artifact` / `summary` / `audit` / `disclaimer`。  
**行情失败：** HTTP 422，`ok: false`，任务状态 `failed`，**不生成假 K 线**。

---

## 环境变量

| 变量 | 含义 |
| --- | --- |
| `CAISHENFOLIO_MARKET_PROVIDER` | `akshare`（默认真实）或 `fixture`（演示） |
| `CAISHENFOLIO_HTTP_TRUST_ENV` | `1` 遵循系统代理；`0` 忽略代理直连 |
| `CAISHENFOLIO_BIND_HOST` / `PORT` | 由 Host 启动时注入 |

---

## C# Host 客户端（Desktop 调用）

`Caishenfolio.Host.Python.AnalyticsCoreClient`：

| 方法 | 对应路由 |
| --- | --- |
| `GetHealthAsync` | GET `/health` |
| `GetMarketDiagnosticsAsync` | GET `/market/diagnostics` |
| `SearchSymbolsAsync` | GET `/symbols/search` |
| `GetMarketBarsAsync` | GET `/market/bars` |
| `RunSymbolSnapshotAsync` | POST `/research/symbol-snapshot` |

仅允许 loopback `BaseAddress`。

---

## 手工试调示例

```powershell
# 健康检查
Invoke-RestMethod http://127.0.0.1:8765/health

# 搜索
Invoke-RestMethod "http://127.0.0.1:8765/symbols/search?q=600000"

# K 线
Invoke-RestMethod "http://127.0.0.1:8765/market/bars?symbol=SSE:600000&start=2024-01-02&end=2024-01-12"

# 研究快照
Invoke-RestMethod http://127.0.0.1:8765/research/symbol-snapshot -Method POST -ContentType "application/json" -Body '{"symbol":"SSE:600000","start":"2024-01-02","end":"2024-01-12"}'
```
