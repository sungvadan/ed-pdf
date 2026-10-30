# pdf-svd

*[Version française](README.md)*

A free, open-source PDF editor for macOS and Windows: read, annotate, sign, rearrange pages… and **edit the existing
text of a PDF like in a word processor**, keeping its font, size and color.

- **macOS**: native Swift app (SwiftUI + PDFKit), with [PDFium](https://pdfium.googlesource.com/pdfium/) for text
  editing.
- **Windows**: C# app (.NET 10 + [Avalonia](https://avaloniaui.net)) with PDFium, same main features.
- Interface in French, English, German, Spanish, Italian, Portuguese (Brazil), Japanese and Simplified Chinese.
- Everything stays on your computer: no account, no upload, no Internet connection.

## Features

- **Reading**: thumbnails, zoom, single page / continuous / two pages, search, bookmarks
- **Annotations**: highlight, underline, strikethrough, notes, formatted text boxes, drawing, shapes, arrows
- **Pages**: rotate, delete, duplicate, reorder by drag and drop, insert PDFs or images, extract
- **Forms**: field filling, with undo
- **Signatures**: drawn, or photographed on paper (the ink is cut out automatically); predefined or custom stamps;
  images
- **Edit text**: click in a paragraph, edit in place, lines reflow, styles are kept; the rest of the page is left
  untouched
- **Redaction**: redacted text is really removed from the file
- **Export for sending**: flattening, password (macOS), compression, author name removal; export as images; size
  reduction
- Undo / redo everywhere (⌘Z on Mac, Ctrl+Z on Windows)

## Installation

Files are in the repository's [*Releases*](../../releases).

### macOS

Download the `.dmg`, open it and drag **pdf-svd** into **Applications**.

The app is not signed with an Apple developer account, so macOS blocks it the first time it is opened. Go to
**System Settings › Privacy & Security** and click **"Open Anyway"** (only once).

Requirements: Mac with Apple silicon, macOS 14 or later.

### Windows

Download `pdf-svd-x64.exe` (Intel/AMD PCs) or `pdf-svd-arm64.exe` (ARM PCs) and run it: nothing to install.

The exe is not signed, so Windows SmartScreen shows "Windows protected your PC". Click **More info › Run anyway**
(only once).

Requirements: Windows 10 or 11, 64-bit (tested on Windows 11).

## Building from source

### macOS

Requirements: Mac with Apple silicon, macOS 14 or later, Apple's *Command Line Tools* (Swift 5.10 or later; Xcode is
not needed: `xcode-select --install`), and an Internet connection for the first build (PDFium download).

```sh
scripts/build-app.sh release --open    # build/pdf-svd.app (debug for faster builds)
scripts/install.sh                     # installs into /Applications
scripts/make-dmg.sh                    # build/pdf-svd-<version>.dmg
```

`build-app.sh` downloads PDFium into `Vendor/pdfium` (pinned version, SHA-256 checked), builds with `swift build`,
assembles the bundle (executable, `Info.plist`, icon, PDFium, licenses) and signs it ad hoc. Plain `swift build` does
not produce an app.

### Windows

Built on the Mac (no Windows needed) with the .NET 10 SDK (`brew install dotnet`):

```sh
scripts/build-windows.sh    # build/windows/pdf-svd-x64.exe and pdf-svd-arm64.exe
```

Single files, PDFium and .NET included. To try it on the Mac: `cd windows && dotnet run -- file.pdf`.

## Code layout

| Folder | Contents |
|---|---|
| `Sources/Folio` | The macOS app (SwiftUI/AppKit): viewer, annotations, pages, signatures, export… |
| `Sources/FolioText` | The text editing engine, without UI: page analysis, font matching, layout, PDF rewriting |
| `Sources/textlab` | Command-line tool to test the engine (`analyze`, `replace`, `render`) |
| `windows/` | The Windows version (C# + Avalonia), including the port of the text engine in `windows/Pdf/Text` |
| `Resources/Localizations` | Translations (French text is the key), shared by both versions |
| `scripts/` | Build, install, disk image, icon, test PDFs, test suite |
| `docs/text-editing.md` | Detailed design of text editing and pitfalls (in French) |

`Folio` is the project's internal code name.

### How text editing works

PDFKit cannot edit page text. pdf-svd uses PDFium to find paragraphs (font, size, color, position) and write the new
text, then removes the old text directly in the page's drawing instructions, operator by operator: the rest of the
page stays pixel-identical. Every change is read back and checked before it is applied.

### Tests

```sh
swift build --product textlab
swift scripts/make-fixtures.swift      # generates test PDFs in build/fixtures
scripts/textlab-suite.sh               # text editing scenarios
```

## Known limitations

- Text editing: not yet on password-protected PDFs, rotated text or rotated pages; a paragraph that grows may overlap
  what follows.
- No digital (certificate) signatures: signatures are visual.
- Windows: no password on export (PDFium cannot encrypt); while editing text, inline bold is displayed as regular (it
  is kept when written).

## Disclaimer

pdf-svd is provided **"as is", without warranty of any kind**, as stated in its [license](LICENSE). Its authors
cannot be held liable for any damage arising from its use, including loss or corruption of data.

- **Keep a copy of your original files** and check the result before sending it.
- **You alone are responsible for how you use the software and for the documents you modify.** pdf-svd is meant to
  correct and complete your own documents, or documents you are authorized to modify. Altering a document without the
  right to do so (contract, invoice, payslip, diploma, official document…) or using such a document may constitute
  forgery, which is a criminal offense in most countries.
- **Signatures are images** placed on the page: they are not electronic signatures within the meaning of the EU eIDAS
  regulation (neither advanced nor qualified) and guarantee neither the signer's identity nor the document's integrity.
- **Redaction**: redacted text is removed from the file, but always check the output (search for the hidden text,
  copy and paste) before sharing it.

## Contributing

Bug reports and pull requests are welcome. By submitting a contribution, you agree that it is published under the
same MIT license.

## License

Code under the [MIT License](LICENSE): you may use, modify and redistribute it freely, including in commercial
projects, provided the copyright notice and license text are kept. It comes with no warranty.

pdf-svd ships [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD license) built by
[pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), which contains other libraries (FreeType,
libjpeg-turbo, zlib, ICU…), and, on Windows, the .NET runtime, Avalonia and SkiaSharp (MIT license). All are under
permissive licenses; the full list is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and their licenses ship with
the app (macOS: `Contents/Resources/Licenses`; Windows: embedded in the exe).

When text is edited, pdf-svd may embed a font installed on the computer into the PDF, as word processors do; fonts
whose license forbids embedding are never used.

PDF is a trademark of Adobe; Apple, Mac and macOS are trademarks of Apple Inc.; Windows is a trademark of Microsoft.
pdf-svd is an independent project, not affiliated with these companies.
