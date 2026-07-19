from __future__ import annotations

import unittest

from caishenfolio_core.market.symbol_index import fuzzy_search_a_share


class FuzzySearchTests(unittest.TestCase):
    def test_pingan_finds_bank_or_group(self) -> None:
        hits = fuzzy_search_a_share("平安", limit=20)
        self.assertTrue(hits, msg="seed/index should find 平安")
        symbols = {h.symbol for h in hits}
        names = " ".join(h.name for h in hits)
        self.assertTrue(
            "SZSE:000001" in symbols or "SSE:601318" in symbols or "平安" in names,
            msg=f"unexpected hits: {[(h.symbol, h.name) for h in hits]}",
        )

    def test_pufa(self) -> None:
        hits = fuzzy_search_a_share("浦发", limit=10)
        self.assertTrue(any(h.symbol == "SSE:600000" or "浦发" in h.name for h in hits))


if __name__ == "__main__":
    unittest.main()
