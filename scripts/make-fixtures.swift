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
