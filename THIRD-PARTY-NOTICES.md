# Third-party notices / Composants tiers

ed-pdf is distributed under the [MIT License](LICENSE). It ships with the following third-party components, all
under permissive licenses. Their full license texts are included in every build:

- macOS: `ed-pdf.app/Contents/Resources/Licenses/`
- Windows: embedded in `ed-pdf.exe` (resources `licenses/…`), and copied to `build/windows/licenses/` by
  `scripts/build-windows.sh`

ed-pdf est distribué sous [licence MIT](LICENSE). Il embarque les composants tiers ci-dessous, tous sous licence
permissive ; leurs textes de licence complets sont livrés avec chaque version (voir ci-dessus).

## macOS and Windows

| Component | License | Copyright |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) | BSD-3-Clause | The PDFium Authors |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (build of PDFium) | MIT | Benoit Blanchon |

Libraries compiled into PDFium:

| Component | License |
|---|---|
| Abseil | Apache-2.0 |
| Anti-Grain Geometry 2.3 | AGG license (permissive) |
| dragonbox | Apache-2.0 with LLVM exception, or Boost 1.0 |
| fast_float | Apache-2.0, MIT or Boost 1.0 |
| FreeType | FreeType License (FTL) |
| HarfBuzz | MIT ("Old MIT") |
| ICU | Unicode License v3 |
| Little CMS | MIT |
| libjpeg-turbo | IJG, BSD-3-Clause, zlib |
| OpenJPEG | BSD-2-Clause |
| libpng | libpng license |
| LLVM libc | Apache-2.0 with LLVM exception |
| simdutf | Apache-2.0 or MIT |
| zlib | zlib license |

## Windows only

| Component | License | Copyright |
|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) (self-contained) | MIT (+ its own third-party notices) | .NET Foundation and Contributors |
| [Avalonia](https://avaloniaui.net) | MIT | The AvaloniaUI Project |
| ANGLE (Avalonia.Angle.Windows.Natives) | BSD-3-Clause | The ANGLE Project Authors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / HarfBuzzSharp | MIT (+ Skia: BSD-3-Clause, Google) | Xamarin, Inc. / Microsoft Corporation |
| MicroCom.Runtime | MIT | Nikita Tsukanov |
| Tmds.DBus.Protocol | MIT | Tom Deseyn |

## Fonts / Polices

ed-pdf does not ship any font. When the text of a PDF is edited, a font installed on the user's computer may be
embedded (as a subset) in the document, as word processors do; fonts whose embedding permissions (OS/2 `fsType`)
forbid it are never embedded.

ed-pdf n'embarque aucune police. Lors d'une modification du texte, une police installée sur l'ordinateur peut être
incorporée (sous-ensemble) dans le document, comme le font les traitements de texte ; les polices dont la licence
interdit l'incorporation (`fsType`) ne sont jamais utilisées.

## Trademarks / Marques

PDF is a trademark of Adobe. Apple, Mac and macOS are trademarks of Apple Inc. Windows is a trademark of Microsoft
Corporation. ed-pdf is an independent project, not affiliated with or endorsed by these companies.
