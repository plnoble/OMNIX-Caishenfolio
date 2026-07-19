# 行情数据源一览（Caishenfolio）

> 原则：**真实数据 or 明确失败**；从不静默伪造 K 线。密钥只存本机 State 目录，不进仓库。

## 状态图例

| 状态 | 含义 |
| --- | --- |
| **已接入** | 代码可用，按依赖/密钥是否就绪决定能否取数 |
| **免费可直用** | 无需你申请（仍依赖公网与上游稳定性） |
| **需你申请** | 注册拿 Token/Key，在软件「数据源与密钥」中填写 |
| **付费/商务** | 可后续接入，需签约，本阶段仅列名预留 |

---

## 一、软件里已经接入的

| 代码 | 名称 | 费用 | 覆盖（大致） | 你需要做什么 |
| --- | --- | --- | --- | --- |
| `akshare` | AkShare（含东方财富/新浪/腾讯等公开源） | **免费** | A股、港股、美股、场内 ETF、部分基金净值 | `pip install akshare pandas`；网络可达上游 |
| `yfinance` | Yahoo Finance 非官方接口 | **免费**（无官方 Key） | 美股、部分港股/全球 | `pip install yfinance`；网络可达 Yahoo |
| `tushare` | Tushare Pro | **免费额度 + 积分/付费点** | A股、指数、部分基金等（视积分） | 官网注册拿 **token**，填入软件 |
| `alphavantage` | Alpha Vantage | **免费档（有限额）+ 付费档** | 美股为主，部分全球 | 官网申请 **API Key**，填入软件 |
| `fixture` | 本地合成演示 | 免费 | 仅 5 个样例标的 | 仅联调 UI，**非真实行情** |
| `auto` | 组合模式（默认） | — | 按优先级尝试已就绪的真实源 | 推荐日常使用 |

**默认优先级（auto）：** `akshare` → `yfinance` → `tushare` → `alphavantage`  
（某一源失败则尝试下一源；全部失败 → fail-closed，不造数。）

### 关于「东方财富」

- **没有**单独接入「东方财富官方开放平台 SDK」。
- 当前 A 股 K 线主要通过 **AkShare 调用东方财富等公开接口**（以及腾讯/日线等回退）。
- 因此状态栏里会看到 `provider=akshare`、provenance 里 `source_api=stock_zh_a_hist` 等。

---

## 二、免费 / 你可自行申请（推荐路线）

| 源 | 怎么拿 | 申请地址（自行打开） | 填到软件哪里 |
| --- | --- | --- | --- |
| AkShare | pip 安装即可 | GitHub/文档站点 | 无需 Key |
| yfinance | pip 安装即可 | — | 无需 Key |
| Tushare token | 注册 + 可能需完善资料/积分 | https://tushare.pro | 「数据源与密钥」→ Tushare |
| Alpha Vantage key | 邮箱领取免费 Key | https://www.alphavantage.co/support/#api-key | 「数据源与密钥」→ Alpha Vantage |

---

## 三、未接入、可后续扩展（需你决定是否签约）

| 源 | 费用倾向 | 说明 | 接入难度 |
| --- | --- | --- | --- |
| 聚宽 / Ricequant 等 | 研究平台账号 | 偏回测社区，非通用行情 | 中 |
| Wind / 同花顺 iFinD / 通达信增值 | **付费终端/API** | 机构级，授权严格 | 高（商务） |
| Bloomberg / Refinitiv | **高价** | 机构 | 高 |
| Polygon / Finnhub / Twelve Data | 免费档有限 + 付费 | 美股/全球 REST | 中（需 Key）
| 券商行情 API | 开户后权限 | 可能含实时，常禁止再分发 | 高（合规） |

本阶段**不**把付费机构源写进默认依赖，避免无授权调用。

---

## 四、本机密钥存放（安全）

- 文件：`%LocalAppData%\Caishenfolio\state\market_credentials.json`
- 启动分析核心时，Host **仅注入进程环境变量**，不写进仓库、不打日志明文。
- 环境变量名：
  - `CAISHENFOLIO_TUSHARE_TOKEN`
  - `CAISHENFOLIO_ALPHAVANTAGE_API_KEY`
  - `CAISHENFOLIO_MARKET_PROVIDER`（`auto` / `akshare` / `yfinance` / `tushare` / `alphavantage` / `fixture`）
  - `CAISHENFOLIO_HTTP_TRUST_ENV`（`0`=忽略坏代理）

---

## 五、依赖安装建议

```powershell
# 免费源常用
pip install "akshare>=1.14.0" "pandas>=2.0" "yfinance>=0.2.40"

# 若使用 Tushare
pip install tushare
```

Alpha Vantage 使用标准库 `urllib`，**不必**额外 pip 包。
