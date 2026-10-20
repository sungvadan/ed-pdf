# Édition du texte existant — conception

Objectif : éditer le texte d'un PDF « comme dans Word » (PDF Expert) : clic dans un paragraphe, édition sur place,
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

- `TextModel.swift` : runs → lignes (même ligne de base, coupure aux gouttières de colonnes) → blocs (paragraphes :
  taille proche, interlignage régulier, recouvrement horizontal). Testé : lettre, deux colonnes justifiées, titres,
  gras en ligne, couleurs.
- `FontResolver.swift` : police embarquée réutilisée si elle contient tous les caractères (couverture et largeurs
  apprises depuis le texte de la page, taille **effective** = Tf × échelle de la matrice) ; sinon même police
  système (PostScript exact, puis famille + traits), sinon Times/Arial/Courier selon la classe. Les polices système
  sont reconstruites en sfnt autonome depuis leurs tables CoreText (gère les .ttc). TrueType (`glyf`) uniquement.
- `BlockWriter.swift` : mise en page gloutonne par mots avec segments stylés (le gras en ligne reste gras),
  alignements gauche/centre/droite/justifié (justifié = un objet par mot), interlignage d'origine.

## Problème découvert → étape B (à faire)

`FPDFPage_GenerateContent` réécrit **tout** le content stream modifié en `Tj` simples : il perd les ajustements
`TJ` (crénage) et l'espacement des mots des textes **non modifiés** (vu sur `colonnes.pdf` : justification cassée
dans le paragraphe voisin). Inacceptable sur des PDF Word/Acrobat.

Vérifié : si on **ajoute seulement** des objets, PDFium garde les streams d'origine **octet pour octet** et écrit les
nouveaux objets dans un stream ajouté à la fin de `/Contents`.

Plan étape B :
1. PDFium : insérer les nouveaux objets (sans supprimer les anciens), `GenerateContent`, sauver → D1.
2. Parseur PDF maison (sortie PDFium : xref classique, pas d'object streams — prévoir xref streams/ObjStm pour
   la lecture plus tard) : trouver la page, ses content streams d'origine, décoder (Flate).
3. Tokeniser le content stream (gérer BI/ID/EI), suivre q/Q/cm, BT/ET, Tf, Tz, Td/TD/Tm/T*, et lister les opérateurs
   d'affichage (Tj, TJ, ', ") dans l'ordre. Le k-ième correspond au k-ième objet texte PDFium de premier niveau
   (vérifier que les nombres concordent, sinon refuser l'édition).
4. Supprimer les opérateurs du paragraphe édité. Si un opérateur conservé suit dans le même BT sans
   repositionnement, remplacer le premier supprimé par `[n] TJ` (déplacement sans dessin : n = −tx·1000/(Tfs·Th),
   tx = déplacement jusqu'au début de l'opérateur conservé exprimé dans l'espace texte via la matrice PDFium
   = Tm×CTM). Ne pas insérer de `Tm` (cela changerait la matrice de ligne Tlm utilisée par T*/Td).
   `'` → `T*`, `aw ac (s) "` → `aw Tw ac Tc T*`.
5. Écrire une mise à jour incrémentale (nouvel objet stream Flate pour chaque stream modifié, xref, trailer /Prev).
6. Vérifier : relire avec PDFium, le nouveau texte est présent et l'ancien absent ; rendu des autres paragraphes
   identique (comparaison de pixels hors zone éditée).

## Étapes suivantes

- C. UI : outil « Modifier le texte » : survol = cadre des paragraphes, clic = NSTextView posé exactement sur le
  paragraphe (polices système équivalentes, attribut de style d'origine par caractère pour la saisie), validation
  → édition → remplacement de page annulable. Barre/inspecteur : police, taille, gras, italique, couleur.
- D. Robustesse : textes dans les Form XObjects, pages pivotées, texte tourné, polices Type3/CFF (OTF), sous-ensemble
  des polices système incorporées (taille du fichier), césure, listes, PDF à xref streams.
