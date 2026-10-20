// Generates test PDFs for text editing into build/fixtures (fonts embedded as subsets, like real-world files).
// Usage: swift scripts/make-fixtures.swift
import AppKit

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let output = root.appendingPathComponent("build/fixtures")
try! FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

func makePDF(_ name: String, pages: [(CGContext) -> Void]) {
    let data = NSMutableData()
    var box = CGRect(x: 0, y: 0, width: 595, height: 842)
    let context = CGContext(consumer: CGDataConsumer(data: data as CFMutableData)!, mediaBox: &box, nil)!
    for draw in pages {
        context.beginPDFPage(nil)
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        draw(context)
        context.endPDFPage()
    }
    context.closePDF()
    try! (data as Data).write(to: output.appendingPathComponent(name))
    print("Wrote build/fixtures/\(name)")
}

func font(_ name: String, _ size: CGFloat) -> NSFont { NSFont(name: name, size: size) ?? .systemFont(ofSize: size) }

func paragraph(_ spacing: CGFloat = 4, alignment: NSTextAlignment = .left) -> NSParagraphStyle {
    let style = NSMutableParagraphStyle()
    style.paragraphSpacing = spacing
    style.alignment = alignment
    return style
}

// 1. Letter: title, mixed bold inline, colored text, serif body, small footer.
makePDF("lettre.pdf", pages: [{ _ in
    NSAttributedString(string: "Objet : Résiliation du contrat n° 2024-118", attributes: [
        .font: font("Helvetica-Bold", 18), .foregroundColor: NSColor(srgbRed: 0.1, green: 0.2, blue: 0.5, alpha: 1),
    ]).draw(in: CGRect(x: 60, y: 740, width: 475, height: 40))

    let body = NSMutableAttributedString(string: "Madame, Monsieur,\n", attributes: [.font: font("Times New Roman", 12), .paragraphStyle: paragraph(10)])
    body.append(NSAttributedString(string: "Par la présente, je vous informe de ma décision de résilier le contrat cité en objet, conformément à l'article 7 des conditions générales. La résiliation prendra effet à compter du ", attributes: [.font: font("Times New Roman", 12), .paragraphStyle: paragraph(10)]))
    body.append(NSAttributedString(string: "31 décembre 2026", attributes: [.font: font("Times New Roman Bold", 12), .paragraphStyle: paragraph(10)]))
    body.append(NSAttributedString(string: ", date d'échéance du préavis de trois mois.\nJe vous remercie de bien vouloir me confirmer la bonne réception de ce courrier et de procéder au remboursement des sommes éventuellement versées d'avance.\nVeuillez agréer, Madame, Monsieur, l'expression de mes salutations distinguées.\n", attributes: [.font: font("Times New Roman", 12), .paragraphStyle: paragraph(10)]))
    body.draw(in: CGRect(x: 60, y: 440, width: 475, height: 280))

    NSAttributedString(string: "Jean Dupont — 12 rue des Lilas, 75011 Paris — jean.dupont@example.com", attributes: [
        .font: font("Helvetica", 8), .foregroundColor: NSColor.darkGray, .paragraphStyle: paragraph(0, alignment: .center),
    ]).draw(in: CGRect(x: 60, y: 40, width: 475, height: 20))
}])

// 2. Two columns of justified text with a centered heading.
let lorem = "La gestion documentaire est au cœur de la productivité des équipes. Un éditeur PDF moderne doit permettre de corriger une coquille, de mettre à jour un montant ou de reformuler une phrase sans repasser par le document source. Les polices embarquées, souvent réduites aux seuls caractères utilisés, compliquent toutefois l'ajout de nouveaux caractères."
makePDF("colonnes.pdf", pages: [{ _ in
    NSAttributedString(string: "Rapport trimestriel", attributes: [.font: font("Avenir Next Demi Bold", 22), .paragraphStyle: paragraph(0, alignment: .center)])
        .draw(in: CGRect(x: 60, y: 760, width: 475, height: 40))
    for column in 0..<2 {
        NSAttributedString(string: lorem + "\n" + lorem, attributes: [.font: font("Georgia", 10.5), .paragraphStyle: paragraph(6, alignment: .justified)])
            .draw(in: CGRect(x: 60 + CGFloat(column) * 250, y: 380, width: 225, height: 360))
    }
}])

// 3. Miscellaneous: ligatures (Hoefler Text), label/value pairs on the same lines, special characters.
makePDF("divers.pdf", pages: [{ _ in
    NSAttributedString(string: "Le fichier officiel définit les affiliations : efficacité, réflexion, offre spéciale.", attributes: [
        .font: font("Hoefler Text", 14), .ligature: 1,
    ]).draw(in: CGRect(x: 60, y: 740, width: 475, height: 60))
    for (row, (label, value)) in [("Nom :", "Jean Dupont"), ("Ville :", "Paris"), ("Montant :", "1 250,00 €")].enumerated() {
        let y = 640 - CGFloat(row) * 22
        NSAttributedString(string: label, attributes: [.font: font("Helvetica", 11)]).draw(at: CGPoint(x: 60, y: y))
        NSAttributedString(string: value, attributes: [.font: font("Helvetica-Bold", 11)]).draw(at: CGPoint(x: 300, y: y))
    }
    NSAttributedString(string: "« Citation » — œuvre complète, coût : 30 € → validé.", attributes: [.font: font("Times New Roman", 12)])
        .draw(at: CGPoint(x: 60, y: 520))
}])

// 3. Invoice printed by Chrome (CID fonts without space glyph, one object per glyph run) — if Chrome is installed.
let chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
let invoiceHTML = output.appendingPathComponent("facture.html")
try! #"""
<!doctype html><html lang="fr"><head><meta charset="utf-8"><style>
@page { size: A4; margin: 2cm; }
body { font-family: Arial, sans-serif; font-size: 11pt; color: #222; }
h1 { font-size: 20pt; color: #1a3d7c; margin: 0 0 8pt; }
p { line-height: 1.35; text-align: justify; }
.total { font-weight: bold; font-size: 13pt; }
.small { font-size: 8pt; color: #666; }
td, th { padding: 4pt 10pt; border-bottom: 1px solid #ccc; text-align: left; }
</style></head><body>
<h1>Facture n° FA-2026-0312</h1>
<p>Client : <b>Société Exemple SARL</b>, 45 avenue de la République, 69003 Lyon.</p>
<p>Conformément à notre devis du 2 octobre 2026, nous vous prions de trouver ci-dessous le détail des prestations réalisées au cours du mois de septembre. Le règlement est attendu sous trente jours à compter de la date d'émission de la présente facture, par virement bancaire.</p>
<table><tr><th>Désignation</th><th>Quantité</th><th>Prix unitaire</th><th>Total</th></tr>
<tr><td>Développement application</td><td>12 jours</td><td>650,00 €</td><td>7 800,00 €</td></tr>
<tr><td>Maintenance</td><td>1 mois</td><td>400,00 €</td><td>400,00 €</td></tr></table>
<p class="total">Total TTC : 9 840,00 €</p>
<p class="small">En cas de retard de paiement, une pénalité égale à trois fois le taux d'intérêt légal sera appliquée, ainsi qu'une indemnité forfaitaire de 40 € pour frais de recouvrement.</p>
</body></html>
"""#.write(to: invoiceHTML, atomically: true, encoding: .utf8)
if FileManager.default.isExecutableFile(atPath: chrome) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: chrome)
    process.arguments = ["--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                         "--print-to-pdf=\(output.appendingPathComponent("facture.pdf").path)", invoiceHTML.absoluteString]
    process.standardError = FileHandle.nullDevice
    try! process.run()
    process.waitUntilExit()
    print("Wrote build/fixtures/facture.pdf (Chrome)")
} else {
    print("Chrome absent : facture.pdf non générée")
}
