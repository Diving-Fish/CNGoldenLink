# Font build inputs

These pinned inputs are extracted unchanged from the supplied
`SapLocalTool.FontGenerator v0.1.1.zip` (`assets/`). The WinForms wrapper is
not needed. `sha256.json` records their SHA-256 hashes; generation checks
them before executing BMFont. No download or system font installation is needed.

- `bmfont64.exe`: AngelCode Bitmap Font Generator, https://www.angelcode.com/products/bmfont/.
  Upstream license notice is in `BMFont-LICENSE.txt`, retrieved from
  https://svn.code.sf.net/p/bmfont/code/trunk/source/main.cpp.
- `Noto Sans CJK SC Medium.otf`: Noto CJK font, https://github.com/notofonts/noto-cjk.
  SIL Open Font License is in `OFL.txt` (upstream `Sans/LICENSE`).
- `chinese.bmfc`: Chinese rasterization settings supplied by the tool (64px,
  256x256 PNG pages, XML font descriptor).
- `chinese.fnt`: supplied base-game glyph metadata, used only to identify
  characters already covered by the vanilla font. It is not shipped in releases.

Only generated `.fnt`/PNG files and the font license are packaged. Build tools,
the OTF and baseline metadata stay out of the release ZIP. To update inputs,
review their provenance, update hashes, regenerate, and verify in-game without
Chinese Font Pack or Extended Chinese Fonts enabled.
