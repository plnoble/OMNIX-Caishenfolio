from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caishenfolio_core.security.path_policy import PathRootKind, PathRootPolicy


class PathPolicyTests(unittest.TestCase):
    def test_resolve_relative_inside_root(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            policy = PathRootPolicy().register(PathRootKind.IMPORT, tmp)
            resolved = policy.resolve(PathRootKind.IMPORT, "notes/a.txt")
            self.assertTrue(str(resolved).startswith(str(Path(tmp).resolve())))
            self.assertTrue(str(resolved).endswith(str(Path("notes") / "a.txt")))

    def test_rejects_traversal(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            policy = PathRootPolicy().register(PathRootKind.ARTIFACT, tmp)
            ok, _, reason = policy.try_resolve(PathRootKind.ARTIFACT, "../outside.txt")
            self.assertFalse(ok)
            self.assertIn("escapes", reason)

    def test_rejects_unc_root(self) -> None:
        policy = PathRootPolicy()
        with self.assertRaises(ValueError):
            policy.register(PathRootKind.STATE, r"\\server\share")


if __name__ == "__main__":
    unittest.main()
