from __future__ import annotations

import unittest

from caishenfolio_core.market.pinyin_index import pinyin_matches, pinyin_of
from caishenfolio_core.market.symbol_index import fuzzy_search_a_share


class PinyinIndexTests(unittest.TestCase):
    def test_seed_names_carry_their_pinyin_offline(self) -> None:
        # Without pypinyin the seed table still romanizes, so fixture mode has real coverage.
        forms = pinyin_of("浦发银行")
        self.assertIsNotNone(forms)
        self.assertEqual(forms.initials, "pfyh")
        self.assertEqual(forms.full, "pufayinhang")

    def test_an_injected_converter_is_used_as_is(self) -> None:
        forms = pinyin_of("测试银行", converter=lambda n: ("csyh", "ceshiyinhang"))
        self.assertEqual(forms.initials, "csyh")
        self.assertEqual(forms.full, "ceshiyinhang")

    def test_an_unknown_name_without_a_converter_yields_no_pinyin(self) -> None:
        """No converter available AND no stored pinyin → None, honestly.

        The old name said "without pypinyin" but never disabled it: in the app's managed
        venv pypinyin is always installed, so the name got romanized and this assertion
        failed (recheck P2-04). The injected converter makes the absence explicit.
        """
        def no_converter(_name: str):
            return None  # the documented "cannot romanize" shape

        self.assertIsNone(pinyin_of("不存在的公司名", converter=no_converter))
        self.assertFalse(pinyin_matches("bxz", "不存在的公司名", converter=no_converter))

    def test_with_a_converter_unknown_names_romanize(self) -> None:
        """The mirror case: pypinyin IS installed (the managed venv always has it)."""
        def stub_converter(name: str):
            return ("bxz", "bucunzai")

        forms = pinyin_of("不存在的公司名", converter=stub_converter)
        self.assertIsNotNone(forms)
        self.assertEqual(forms.initials, "bxz")
        self.assertTrue(pinyin_matches("bxz", "不存在的公司名", converter=stub_converter))

    def test_matching_requires_an_ascii_letter_query(self) -> None:
        self.assertFalse(pinyin_matches("平安", "平安银行"))
        self.assertFalse(pinyin_matches("600", "平安银行"))
        self.assertTrue(pinyin_matches("pfyh", "浦发银行"))
        self.assertTrue(pinyin_matches("pufa", "浦发银行"))
        # A short fragment inside the full pinyin needs at least 3 letters.
        self.assertTrue(pinyin_matches("yinhang", "浦发银行"))
        self.assertFalse(pinyin_matches("fa", "浦发银行"))


class PinyinSearchTests(unittest.TestCase):
    def test_initials_find_pufa_bank(self) -> None:
        hits = fuzzy_search_a_share("pfyh", limit=10)
        self.assertTrue(any("浦发" in h.name for h in hits), msg=[(h.symbol, h.name) for h in hits])

    def test_full_pinyin_prefix_finds_pingan(self) -> None:
        hits = fuzzy_search_a_share("pingan", limit=10)
        self.assertTrue(any("平安" in h.name for h in hits), msg=[(h.symbol, h.name) for h in hits])

    def test_initials_find_chinese_names_still_rank_first(self) -> None:
        # The Chinese query keeps its old behaviour; pinyin adds, never replaces.
        hits = fuzzy_search_a_share("平安", limit=20)
        self.assertTrue(any("平安" in h.name for h in hits))


if __name__ == "__main__":
    unittest.main()
