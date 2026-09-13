# Third-party notices

## Noto Sans CJK SC

UN_Nexo self-contained desktop builds include **Noto Sans CJK SC Regular** as a fallback font so Chinese text does not depend on fonts installed by the operating system.

- Upstream: Google / Adobe Noto CJK project (`notofonts/noto-cjk`)
- Pinned upstream commit: `f8d157532fbfaeda587e826d4cd5b21a49186f7c`
- Bundled file: `Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf`
- License: SIL Open Font License 1.1 (OFL-1.1)

The font is downloaded from the pinned upstream commit during the build and embedded as an Avalonia resource. The font binary is intentionally not committed to this repository.
