# Édition du texte existant — conception

Objectif : éditer le texte d'un PDF « comme dans Word » : clic dans un paragraphe, édition sur place,
même police / taille / couleur, reflux du paragraphe, styles conservés. Pas de « cache blanc + zone de texte ».

## Architecture

- **PDFKit** reste le moteur d'affichage, d'annotations et d'undo de l'app.
- **PDFium** (`Vendor/pdfium`, via `scripts/fetch-pdfium.sh`, version épinglée + SHA-256) sert à :
  - analyser la page : objets texte, police (nom, embarquée, gras/italique), taille effective, couleur, position ;
  - créer le nouveau texte (objets texte + incorporation de polices, `FPDFText_LoadFont` en CID).
- **Chirurgie maison des content streams** (à écrire, voir « Étape B ») pour supprimer l'ancien texte.
- Une édition produit un nouveau PDF ; l'app remplace la page éditée (`applyPages`) → ⌘Z fonctionne.

Module `FolioText` (bibliothèque, sans UI) + outil `textlab` (CLI de test) :

```
swift build --product textlab
swift scripts/make-fixtures.swift            # PDF de test dans build/fixtures
.build/debug/textlab analyze build/fixtures/lettre.pdf
.build/debug/textlab replace build/fixtures/lettre.pdf 1 2 "trois mois" "deux mois" /tmp/out.pdf [--system]
.build/debug/textlab render /tmp/out.pdf 1 /tmp/out.png
```

## Ce qui marche (étape A)

- `TextModel.swift` : runs → lignes (même ligne de base, coupure aux gouttières de colonnes et aux changements de couleur espacés de plus de 0,6 em, ex. « B2 » noir sur case blanche + titre blanc sur bandeau) → blocs (paragraphes :
  taille proche, interlignage régulier, recouvrement horizontal). Testé : lettre, deux colonnes justifiées, titres,
  gras en ligne, couleurs.
- `FontResolver.swift` : police embarquée réutilisée si elle contient tous les caractères (couverture apprise depuis le
  texte de la page ; largeurs **exactes** mesurées par PDFium : droite(texte + « o ») − droite(« o »), car les bornes d'un
  objet s'arrêtent à l'encre du dernier glyphe ; taille **effective** = Tf × échelle de la matrice) ; sinon même police
  système (PostScript exact, puis famille + traits), sinon Times/Arial/Courier selon la classe. Les polices système
  sont reconstruites en sfnt autonome depuis leurs tables CoreText (gère les .ttc). TrueType (`glyf`) uniquement.
- `BlockWriter.swift` : mise en page gloutonne par mots avec segments stylés (le gras en ligne reste gras),
  alignements gauche/centre/droite/justifié (justifié = un objet par mot), interlignage d'origine.

## Étape B (faite) : chirurgie des content streams

`FPDFPage_GenerateContent` réécrit tout un content stream modifié en `Tj` simples et perd le crénage `TJ`/l'espacement
des textes **non modifiés**. Si on ne fait qu'**ajouter** des objets, PDFium garde les streams d'origine octet pour
octet et écrit le nouveau texte dans un stream ajouté. D'où `TextEditor.replace` :

1. PDFium ajoute les nouveaux objets (sans supprimer les anciens) → sauvegarde intermédiaire.
2. `PDFFile` (parseur maison : xref classiques et compressées, object streams, Flate + prédicteurs PNG) retrouve
   la page et ses content streams.
3. `ContentSurgery` tokenise (BI/ID/EI gérés), suit q/Q, BT/ET, Tf, Tz et les repositionnements, liste les
   opérateurs Tj/TJ/'/" ; le k-ième qui affiche au moins un caractère = k-ième objet texte PDFium (nombres vérifiés,
   sinon erreur). Les opérateurs du paragraphe sont supprimés ; si un opérateur conservé suit dans le même BT sans
   repositionnement, le premier supprimé devient `[n] TJ` (déplacement identique, sans dessin, Tlm inchangé).
4. Mise à jour incrémentale (stream Flate + xref + trailer /Prev).
5. Vérification avec **PDFKit** (`page.string` contient le nouveau texte) ; sinon nouvel essai avec polices système ;
   sinon `TextEditError.verificationFailed` (on ne produit jamais un PDF faux).

Résultat : 0 pixel différent hors du paragraphe édité (lettre, colonnes justifiées, facture Chrome).

### Pièges rencontrés (et réglés)

- Taille effective = Tf × échelle de la matrice (CoreText écrit `1 Tf` avec une matrice ×12).
- Largeurs : ni les écarts entre caractères (crénage) ni les boîtes « loose » ne sont exacts (erreur cumulée visible au
  changement de style) → mesure par objet temporaire PDFium (voir ci-dessus), écart < 0,3 pt sur 80 caractères.
- Espace entre deux runs : écart géométrique (boîte loose du dernier glyphe → glyphe suivant) > 15 % de la taille ;
  l'extraction PDFium invente une espace à chaque changement de police (« 2026 , »).
- Couverture d'une police embarquée : caractères réellement dessinés (`FPDFText_IsGenerated` exclu). Les sous-ensembles
  Chrome n'ont **pas de glyphe espace** → mots en objets séparés, l'écart fait l'espace.
- Polices système incorporées par PDFium : sa ToUnicode choisit parfois U+00A0 pour l'espace ou U+00AD pour « - »
  (glyphe partagé) → on reconstruit la `cmap` (format 12) avec un seul caractère par glyphe, hors caractères de contrôle.
- PDFKit colle les mots séparés seulement par un écart à certains changements de police → on écrit de vrais espaces
  dès que la police en a un (y compris en justifié : un objet par mot avec son espace).
- Les espaces entre runs se déduisent de l'extraction PDFium (`followedBySpace`), pas des boîtes d'encre.

Tests : `swift scripts/make-fixtures.swift` puis `scripts/textlab-suite.sh` (facture.pdf : générée par Chrome sans
fenêtre depuis `build/fixtures/facture.html`, voir l'historique de session ou régénérer avec
`"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --no-pdf-header-footer --print-to-pdf=…`).

Limites connues : un paragraphe qui grandit peut chevaucher le contenu suivant ; documents chiffrés et xref compressées
en sortie PDFium non gérés par l'écriture incrémentale (PDFium écrit des xref classiques, OK) ; texte dans les Form
XObjects, texte tourné : lecture seule.

## Étapes suivantes

- C. ✅ UI (`Sources/Folio/TextEditing.swift`) : outil « Modifier le texte » ; survol = cadre du paragraphe ; clic =
  NSTextView posé sur le paragraphe (première ligne de base alignée, police d'affichage = `TextDisplay.font`, attribut
  `.folioRunStyle` par caractère → la saisie hérite du style) ; clic ailleurs / changement
  d'outil / zoom = validation, Échap = validation aussi (⌘Z pour revenir) ; validation = `TextEditor.replace` + `applyPages` (⌘Z).
  Mise en page analysée une fois par révision (`ViewerModel.textLayout(for:)`).
  Auto-test sans UI du vrai code de l'app : `build/pdf-svd.app/Contents/MacOS/Folio --selftest-edit in.pdf "trouver" "remplacer" out.pdf`
  (`FOLIO_SNAPSHOT=x.png` capture l'éditeur).
- C2. À faire : contrôles de style pendant l'édition (police, taille, gras, italique, couleur — `RunStyle.bold/italic`
  existent déjà côté moteur), nouveau paragraphe de texte « natif », déplacer/redimensionner un paragraphe.
- D. Robustesse : textes dans les Form XObjects, pages pivotées, texte tourné, polices Type3/CFF (OTF), sous-ensemble
  des polices système incorporées (taille du fichier), césure, listes, PDF à xref streams.
