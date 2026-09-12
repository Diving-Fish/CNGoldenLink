import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("fonts", Path(__file__).with_name("generate-fonts.py"))
fonts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fonts)


class FontTests(unittest.TestCase):
    def test_unicode_selection(self):
        self.assertEqual(fonts.required_characters("\ufeff榜榜\n\r\t𠮷"), {ord("榜"), ord("𠮷")})

    def test_output_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "chinese.fnt"
            path.write_text('<font><pages><page id="0" file="page.png"/></pages>'
                            f'<chars><char id="{ord("榜")}" page="0" width="40" height="50"/></chars></font>')
            with self.assertRaisesRegex(ValueError, "PNG"):
                fonts.validate_font(path, {ord("榜")})
            (path.parent / "page.png").write_bytes(b"\x89PNG\r\n\x1a\n")
            self.assertEqual(len(fonts.validate_font(path, {ord("榜")})), 1)
            with self.assertRaisesRegex(ValueError, "Missing generated glyphs"):
                fonts.validate_font(path, {ord("榜"), ord("授")})
            path.write_text(path.read_text().replace('page="0"', 'page="1"'))
            with self.assertRaisesRegex(ValueError, "unknown page"):
                fonts.validate_font(path, {ord("榜")})

    def test_no_missing_clears_stale_supplement(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            (output / "chinese.fnt").write_text("stale")
            (output / "cngoldenlink_chinese_0.png").write_bytes(b"stale")
            with patch.object(fonts, "OUTPUT", output), patch.object(fonts, "required_characters", return_value=set()), \
                    patch.object(fonts.subprocess, "run") as run:
                fonts.generate()
                run.assert_not_called()
            self.assertEqual(list(output.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
