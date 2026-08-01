# Current Development

- Project: **OMNIX-Caishenfolio**
- Status: **R0~R5 + V1~V4 + A1~A4 + S1~S2 + F1~F5 + G1~G4 + H1~H4 瀹屾垚 (v0.13.1 / R5)**
- 搴旂敤鍐呮洿鏂板凡鍚敤锛氫笅杞?鈫?SHA-256 鏍￠獙 鈫?ECDSA P-256 楠岀 鈫?瀹夎锛涗换涓€灞備笉杩囧嵆鍒犻櫎鏂囦欢
- 瑕嗙洊: A鑲?/ 娓偂 / 缇庤偂 / 鏃ヨ偂锛涜偂绁?/ ETF / 鍦哄鍩洪噾 / 鍊哄埜 / 鍙浆鍊?/ 澶栨眹 / 鐜伴噾

## 宸蹭氦浠?
- **R0~R5**锛氭暟鎹涔?鈫?璐︽湰鍐呮牳 鈫?浼板€间笌鏀剁泭 鈫?琛屾儏閫氶亾 鈫?妗岄潰鍙屼富绾?鈫?CSV 瀵煎叆瀵煎嚭
- **V1~V4**锛氱増鏈彿鍗曚竴鏉ユ簮锛坄VERSION` / `PHASE` + 婕傜Щ瀹堟姢娴嬭瘯锛夈€乄iX MSI 鎵撳寘銆?  搴旂敤鍐呮洿鏂版鏌ワ紙GitHub Releases锛夈€丆I / Release 宸ヤ綔娴?- **A1~A4**锛堢Щ妞嶈嚜褰掓。 FinWorkbench锛夛細Python 杩愯鏃惰嚜鍔ㄤ緵缁欙紙uv/venv + 渚濊禆鍝堝笇锛夈€?  UI 鍚姩鍐掔儫鑴氭湰銆佺粍鍚堥闄╂寚鏍囷紙闆嗕腑搴?鍥炴挙/閰嶇疆鍋忕锛夈€佷环鏍兼彁閱?- **S1~S2**锛氬亸濂借缃寔涔呭寲锛坰chema v5 `settings`锛変笌璁剧疆绐楀彛
- **F1~F5**锛氭妧鏈寚鏍囧簱銆佽瘹瀹炲洖娴嬶紙鎴愭湰/鏍锋湰澶?鍥炴挙/杩炰簭锛夈€佷及鍊煎垎浣嶄笌鍩烘湰闈€?  姹囩巼鍒╁樊鐪嬫澘銆佹墦鏂拌褰?- **G1~G4**锛氳В璇诲眰锛堝垎妗?+ 鐧借瘽瑙ｉ噴 + 鍘嗗彶鏉′欢鏀剁泭锛夈€佹墦鏂版棩鍘嗐€佺晫闈㈠叏鎺ャ€佸洖娴嬫姤鍛婇〉
- **H1~H4**锛?  - H1 绫诲瀷鍖栨暟鎹簮閿欒锛坄market/errors.py`锛? 浠呭鍙噸璇曢敊璇€€閬块噸璇?  - H2 绗簩鏁版嵁婧愶細baostock锛圓鑲″巻鍙诧級銆佸ぉ澶╁熀閲?pingzhongdata锛堝満澶栧熀閲戝噣鍊硷紝绾爣鍑嗗簱锛?  - H3 鐪熷疄鏀跨瓥鍒╃巼锛氱航绾﹁仈鍌?EFFR / 娆уぎ琛?MRO / 棣欐腐閲戠灞€ / LPR / 鏃ユ湰澶锛?    缂撳瓨涓€澶╋紝鍙栨暟澶辫触鍥炶惤鍐呯疆鍊煎苟鏍囪 `stale`锛堢晫闈㈡爣绾級
  - H4 澶栭儴閫氱煡锛氫紒涓氬井淇?椋炰功/閽夐拤/Telegram/Discord/Slack/鑷畾涔?webhook + SMTP 閭欢锛?    `--notify` 鏃犵晫闈㈡ā寮?+ Windows 璁″垝浠诲姟娉ㄥ唽锛堜粎鐢ㄦ埛鐐瑰嚮鏃舵墽琛岋級锛?    鍑嵁缁?DPAPI 鍔犲瘑鍚庢墠鍐欏叆璐︽湰

## 楠岃瘉

```powershell
dotnet build Caishenfolio.slnx; if ($?) { dotnet test Caishenfolio.slnx }   # 338 閫氳繃 / 0 璀﹀憡
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
python -m unittest discover -s tests/python -p "test_*.py"                  # 316 閫氳繃
scripts\ui_smoke.ps1                             # XAML 鏀瑰姩蹇呰窇锛涘惈璁剧疆绐楀彛鍔犺浇妫€鏌?dotnet build packaging\windows\Omnix.Installer.wixproj -c Release           # 鍑?MSI
```

鍚庡彴妫€鏌ユ墜宸ラ獙璇侊細`Caishenfolio.Desktop.exe --notify` 搴旀棤绐楀彛閫€鍑猴紙code 0锛夛紝
骞跺湪 `%LOCALAPPDATA%\Caishenfolio\logs\notify.log` 杩藉姞涓€琛屻€?
## 鏈仛

- 鏃х爺绌堕〉锛?225 琛?code-behind锛夋湭 MVVM 鍖?- akshare 鍊哄埜 / 姹囩巼鎺ュ彛鏈仈缃戦獙璇?- `release_bundle.ps1`锛坢anifest + checksums + 鍒嗘鎶ュ憡锛夋湭绉绘锛屽綋鍓嶇敱 release.yml 绠€鍖栨壙鎷?- 鍑€鍊兼洸绾垮皻鏃犲浘琛ㄥ睍绀猴紙鏁版嵁宸插瓨鍦?`valuation_snapshots`锛屽彧鐢ㄤ簬绠楀洖鎾わ級
- 浼板€煎垎浣嶄粎 A 鑲★紙涓婃父鏁版嵁闄愬埗锛?- 鍚庡彴 `--notify` 鍙煡鎵撴柊鏃堕檺锛涗环鏍?闆嗕腑搴︽彁閱掗渶瑕佸垎鏋愭牳蹇冿紝鏈湪鏃犵晫闈㈡ā寮忎笅鍚姩
- 閫氱煡璁剧疆鐣岄潰鐩墠鍙敮鎸佷竴涓?webhook 娓犻亾锛堟暟鎹ā鍨嬪凡鏀寔澶氫釜锛?
- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio

