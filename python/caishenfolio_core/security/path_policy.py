from __future__ import annotations

from enum import StrEnum
from pathlib import Path


class PathRootKind(StrEnum):
    IMPORT = "import"
    ARTIFACT = "artifact"
    RUN = "run"
    STATE = "state"


class PathRootPolicy:
    def __init__(self) -> None:
        self._roots: dict[PathRootKind, Path] = {}

    def register(self, kind: PathRootKind, root_path: str | Path) -> PathRootPolicy:
        root = Path(root_path)
        text = str(root)
        if text.startswith("\\\\") or text.startswith("//"):
            raise ValueError("UNC paths are not allowed as path roots.")
        resolved = root.expanduser().resolve()
        resolved.mkdir(parents=True, exist_ok=True)
        self._roots[kind] = resolved
        return self

    def get_root(self, kind: PathRootKind) -> Path:
        if kind not in self._roots:
            raise KeyError(f"Path root '{kind}' is not registered.")
        return self._roots[kind]

    def try_resolve(self, kind: PathRootKind, candidate: str | Path) -> tuple[bool, Path | None, str]:
        if candidate is None or str(candidate).strip() == "":
            return False, None, "Path is empty."

        text = str(candidate)
        if text.startswith("\\\\") or text.startswith("//"):
            return False, None, "UNC paths are rejected."

        if kind not in self._roots:
            return False, None, f"Path root '{kind}' is not registered."

        root = self._roots[kind]
        path = Path(candidate)
        try:
            full = path.resolve() if path.is_absolute() else (root / path).resolve()
        except OSError as exc:
            return False, None, f"Path could not be resolved: {exc}"

        try:
            full.relative_to(root)
        except ValueError:
            return False, None, f"Path escapes allowed root '{kind}'."

        return True, full, ""

    def resolve(self, kind: PathRootKind, candidate: str | Path) -> Path:
        ok, full, reason = self.try_resolve(kind, candidate)
        if not ok or full is None:
            raise ValueError(reason)
        return full
