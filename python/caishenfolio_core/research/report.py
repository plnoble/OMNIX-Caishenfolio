from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def build_markdown_report(
    *,
    title: str,
    symbol: str | None,
    sections: list[dict[str, Any]],
    product: str = "OMNIX-Caishenfolio",
) -> str:
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    lines = [
        f"# {title}",
        "",
        f"- 产品：{product}",
        f"- 生成时间：{now}",
        f"- 主标的：{symbol or '（多标的/组合）'}",
        "",
        "> **研究/模拟结论，非投资建议。**",
        "",
    ]
    for sec in sections:
        lines.append(f"## {sec.get('heading', '章节')}")
        lines.append("")
        body = sec.get("body")
        if isinstance(body, str):
            lines.append(body)
            lines.append("")
        elif isinstance(body, list):
            for item in body:
                lines.append(f"- {item}")
            lines.append("")
        elif isinstance(body, dict):
            for k, v in body.items():
                lines.append(f"- **{k}**：{v}")
            lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("*本报告由 OMNIX-Caishenfolio 本地生成，仅供研究模拟。*")
    lines.append("")
    return "\n".join(lines)


def write_report(
    markdown: str,
    artifact_root: str | Path,
    filename: str | None = None,
) -> dict[str, object]:
    root = Path(artifact_root)
    root.mkdir(parents=True, exist_ok=True)
    name = filename or f"report_{datetime.now(timezone.utc).strftime('%Y%m%d_%H%M%S')}.md"
    if not name.endswith(".md"):
        name += ".md"
    # safety: no path traversal
    name = Path(name).name
    path = root / name
    path.write_text(markdown, encoding="utf-8")
    # also simple HTML wrap for double-click open
    html_path = path.with_suffix(".html")
    html = (
        "<!DOCTYPE html><html><head><meta charset='utf-8'>"
        f"<title>{name}</title>"
        "<style>body{font-family:Segoe UI,sans-serif;max-width:900px;margin:24px auto;"
        "background:#0f1419;color:#e7ecf1;line-height:1.5}"
        "pre,code{white-space:pre-wrap}</style></head><body><pre>"
        + markdown.replace("&", "&amp;").replace("<", "&lt;")
        + "</pre></body></html>"
    )
    html_path.write_text(html, encoding="utf-8")
    return {
        "ok": True,
        "markdown_path": str(path),
        "html_path": str(html_path),
        "filename": name,
    }
